using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Trainer.Contracts.Storage;
using Trainer.Web.Client.Storage;

namespace Trainer.Web.Client.Tests;

/// <summary>
/// An in-memory stand-in for <c>outbox.js</c> with the same document rules (merge and remove by id, destination-first
/// move, version and revision checks, quarantine). It exists to test how the C# stores react to results; the lock and
/// cross-tab semantics themselves are tested against the real script in <c>OutboxConcurrencyTests</c>.
/// </summary>
internal sealed class FakeBrowserStore : IBrowserStore
{
    private const int Version = 1;

    private static readonly Dictionary<string, (string List, string Id)> Shapes = new(StringComparer.Ordinal)
    {
        ["outbox"] = ("answers", "clientAnswerId"),
        ["summaries"] = ("items", "mockSessionId"),
        ["history"] = ("items", "id"),
    };

    // Parsed documents are kept as nodes so that thousands of appends do not re-parse a growing string each time.
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    // Id positions per cached list, so a long run of appends stays linear.
    private readonly ConditionalWeakTable<JsonArray, Dictionary<string, int>> _indexes = [];
    private long _clock = 1_700_000_000_000;

    /// <summary>False makes every mutation answer <c>no-locks</c>, as <c>outbox.js</c> does without <c>navigator.locks</c>.</summary>
    public bool LocksAvailable { get; set; } = true;

    /// <summary>True makes every write that grows a value fail with <c>quota</c>; shrinking writes and removals still work.</summary>
    public bool QuotaFull { get; set; }

    /// <summary>Keys whose shrinking writes fail with <c>write-failed</c>: a <c>remove</c> on them and the source half of a <c>move</c>.</summary>
    public HashSet<string> FailingRemovals { get; } = new(StringComparer.Ordinal);

    public int Writes { get; private set; }

    public List<StoreOp> Ops { get; } = [];

    public IEnumerable<string> Keys => _slots.Keys;

    public string? Raw(string key) => _slots.TryGetValue(key, out Slot? slot) ? slot.Text : null;

    public void SetRaw(string key, string value) => _slots[key] = new Slot(value, null);

    public ValueTask<string?> ReadAsync(string key) => ValueTask.FromResult(Raw(key));

    /// <summary>Counts how often the fingerprint of a key was asked for; it replaces reading the log in the static edition.</summary>
    public int Fingerprints { get; private set; }

    public ValueTask<string?> FingerprintAsync(string key)
    {
        Fingerprints++;
        Classified current = Classify(key);
        if (current.Refusal is not null)
        {
            return ValueTask.FromResult<string?>(null);
        }

        return ValueTask.FromResult<string?>(FingerprintOf(current.Doc?["answers"] as JsonArray ?? []));
    }

    // Any stable function of the count and the tail will do for the tests; outbox.js has its own.
    private static string FingerprintOf(JsonArray items)
    {
        string tail = string.Concat(items.Skip(Math.Max(0, items.Count - 64)).Select(e => e?.ToJsonString() + ";"));
        return items.Count + ":" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tail)))[..8];
    }

    public ValueTask<StoreResult> MutateAsync(StoreOp op)
    {
        ArgumentNullException.ThrowIfNull(op);
        Ops.Add(op);
        if (!LocksAvailable)
        {
            return ValueTask.FromResult(Fail(StoreErrors.NoLocks));
        }

        try
        {
            return ValueTask.FromResult(Apply(op));
        }
        catch (QuotaException)
        {
            return ValueTask.FromResult(Fail(StoreErrors.Quota));
        }
    }

    private StoreResult Apply(StoreOp op) => op.Op switch
    {
        "append" => Append(op.Key, "outbox", op.Value!.Value),
        "summaryAdd" => Append(op.Key, "summaries", JsonSerializer.SerializeToElement(new[] { op.Value!.Value })),
        "historyAdd" => HistoryAdd(op.Key, op.Value!.Value),
        "remove" => Remove(op.Key, "outbox", op.Ids!),
        "summaryRemove" => Remove(op.Key, "summaries", op.Ids!),
        "move" => Move(op),
        "putIfRevision" => PutIfRevision(op),
        "put" => Put(op),
        "delete" => Delete(op),
        "quarantine" => Quarantine(op.Key),
        _ => Fail(StoreErrors.BadOp),
    };

    private StoreResult Append(string key, string shape, JsonElement entries)
    {
        (string list, string id) = Shapes[shape];
        if (!TryList(key, list, out JsonObject? doc, out JsonArray? items, out StoreResult? refusal))
        {
            return refusal!;
        }

        JsonArray added = (JsonArray)JsonNode.Parse(entries.GetRawText())!;
        string previous = FingerprintOf(items!);
        int newOnes = Merge(items!, added, id);
        Commit(key, doc!, list);
        return shape == "outbox"
            ? new StoreResult(true, true, null, null, added.Count, FingerprintOf(items!), previous, newOnes)
            : Done(added.Count);
    }

    // outbox.js: bad-op without a string id, stack or finishedAt; merge by id, newest finishedAt first (then id), only the
    // newest 20 of each stack stay; an unreadable finishedAt on the entry being added counts as now.
    private StoreResult HistoryAdd(string key, JsonElement entry)
    {
        (string list, string id) = Shapes["history"];
        JsonObject added = (JsonObject)JsonNode.Parse(entry.GetRawText())!;
        if (IdOf(added, id) is null || added["stack"] is not JsonValue stack || !stack.TryGetValue<string>(out _)
            || added["finishedAt"] is not JsonValue finished || !finished.TryGetValue<string>(out _))
        {
            return Fail(StoreErrors.BadOp);
        }

        if (FinishedAt(added) is null)
        {
            added["finishedAt"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (!TryList(key, list, out JsonObject? doc, out JsonArray? items, out StoreResult? refusal))
        {
            return refusal!;
        }

        Merge(items!, [added], id);
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        List<JsonNode?> kept =
        [
            .. items!
                .OrderByDescending(e => FinishedAt(e) ?? DateTimeOffset.MinValue)
                .ThenByDescending(e => IdOf(e, id) ?? "", StringComparer.Ordinal)
                .Where(e =>
                {
                    string group = e is JsonObject o && o["stack"] is JsonValue s && s.TryGetValue(out string? text) ? text : "";
                    int seen = counts.GetValueOrDefault(group);
                    counts[group] = seen + 1;
                    return seen < StoredMockHistory.MaxItems;
                })
                .Select(e => e!.DeepClone()),
        ];
        _indexes.Remove(items!);
        doc![list] = new JsonArray([.. kept]);
        Commit(key, doc, list);
        return Done(kept.Any(e => IdOf(e, id) == IdOf(added, id)) ? 1 : 0);
    }

    // The JS reads the instant with Date.parse after making the fraction three digits; DateTimeOffset.TryParse accepts the
    // same ISO forms, and what it rejects counts as no time.
    private static DateTimeOffset? FinishedAt(JsonNode? entry) =>
        entry is JsonObject value && value["finishedAt"] is JsonValue text && text.TryGetValue(out string? raw)
            && DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTimeOffset at)
                ? at
                : null;

    private StoreResult Remove(string key, string shape, IReadOnlyList<string> ids)
    {
        if (FailingRemovals.Contains(key))
        {
            return Fail(StoreErrors.WriteFailed);
        }

        (string list, string id) = Shapes[shape];
        if (!TryList(key, list, out JsonObject? doc, out JsonArray? items, out StoreResult? refusal))
        {
            return refusal!;
        }

        HashSet<string> remove = new(ids, StringComparer.Ordinal);
        int removed = RemoveWhere(items!, e => IdOf(e, id) is string value && remove.Contains(value));
        Commit(key, doc!, list);
        return Done(removed);
    }

    private StoreResult Move(StoreOp op)
    {
        (string list, string id) = Shapes[op.Shape ?? "outbox"];
        string to = op.To!;
        if (string.Equals(op.Key, to, StringComparison.Ordinal))
        {
            return Done(0);
        }

        if (!TryList(op.Key, list, out JsonObject? fromDoc, out JsonArray? fromItems, out StoreResult? refusal)
            || !TryList(to, list, out JsonObject? toDoc, out JsonArray? toItems, out refusal))
        {
            return refusal!;
        }

        HashSet<string>? only = op.Ids is null ? null : new HashSet<string>(op.Ids, StringComparer.Ordinal);
        HashSet<string> claimed = op.Shape is null or "outbox" ? ClaimedElsewhere(op.Key, to, list, id) : [];
        JsonArray moving = [];
        HashSet<string> dropping = new(StringComparer.Ordinal);
        foreach (JsonNode? entry in fromItems!)
        {
            if (IdOf(entry, id) is not string value || (only is not null && !only.Contains(value)))
            {
                continue;
            }

            if (claimed.Contains(value))
            {
                dropping.Add(value);
                continue;
            }

            JsonObject copy = (JsonObject)entry!.DeepClone();
            if (op.Value is JsonElement patch)
            {
                foreach (KeyValuePair<string, JsonNode?> field in (JsonObject)JsonNode.Parse(patch.GetRawText())!)
                {
                    copy[field.Key] = field.Value?.DeepClone();
                }
            }

            moving.Add(copy);
        }

        if (moving.Count == 0 && dropping.Count == 0)
        {
            return Done(0);
        }

        HashSet<string> movedIds = new(moving.Select(e => IdOf(e, id)!), StringComparer.Ordinal);
        if (moving.Count > 0)
        {
            Merge(toItems!, moving, id);
            Commit(to, toDoc!, list);
        }

        if (FailingRemovals.Contains(op.Key))
        {
            return new StoreResult(false, true, StoreErrors.WriteFailed, null, movedIds.Count);
        }

        RemoveWhere(fromItems!, e => IdOf(e, id) is string value && (movedIds.Contains(value) || dropping.Contains(value)));
        try
        {
            Commit(op.Key, fromDoc!, list);
        }
        catch (QuotaException)
        {
            return new StoreResult(false, true, StoreErrors.Quota, null, movedIds.Count);
        }

        return Done(movedIds.Count);
    }

    // outbox.js: an answer already in another owner's outbox (left there by an earlier move whose source removal failed)
    // is that owner's; a move only removes it from the source.
    private HashSet<string> ClaimedElsewhere(string from, string to, string list, string id)
    {
        HashSet<string> claimed = new(StringComparer.Ordinal);
        foreach (string key in _slots.Keys.Where(k => k.StartsWith("dit.outbox.v1.", StringComparison.Ordinal)
            && !k.StartsWith(StorageKeys.CorruptPrefix, StringComparison.Ordinal)
            && k != from && k != to))
        {
            if (Classify(key).Doc?[list] is JsonArray items)
            {
                claimed.UnionWith(items.Select(e => IdOf(e, id)).OfType<string>());
            }
        }

        return claimed;
    }

    private StoreResult PutIfRevision(StoreOp op)
    {
        Classified current = Classify(op.Key);
        if (current.Refusal is not null)
        {
            return current.Refusal;
        }

        long revision = current.Doc?["revision"]?.GetValue<long>() ?? 0;
        bool otherSession = op.SessionId is not null
            && current.Doc is not null
            && !string.Equals(current.Doc["sessionId"]?.GetValue<string>(), op.SessionId, StringComparison.Ordinal);
        if (revision != op.ExpectedRevision || otherSession)
        {
            return new StoreResult(false, false, StoreErrors.Revision, revision, 0);
        }

        JsonObject value = (JsonObject)JsonNode.Parse(op.Value!.Value.GetRawText())!;
        WriteText(op.Key, value.ToJsonString());
        return new StoreResult(true, true, null, value["revision"]!.GetValue<long>(), 1);
    }

    private StoreResult Put(StoreOp op)
    {
        Classified current = Classify(op.Key);
        if (current.Refusal is { Error: StoreErrors.NewerVersion })
        {
            return current.Refusal;
        }

        WriteText(op.Key, op.Value!.Value.GetRawText());
        return Done(1);
    }

    private StoreResult Delete(StoreOp op)
    {
        Classified current = Classify(op.Key);
        if (current.Refusal is not null)
        {
            return current.Refusal;
        }

        if (current.Doc is null
            || (op.SessionId is not null && !string.Equals(current.Doc["sessionId"]?.GetValue<string>(), op.SessionId, StringComparison.Ordinal)))
        {
            return Done(0);
        }

        _slots.Remove(op.Key);
        Writes++;
        return Done(1);
    }

    private StoreResult Quarantine(string key)
    {
        string? raw = Raw(key);
        if (raw is null || Classify(key).Refusal is not { Error: StoreErrors.Corrupt })
        {
            return Done(0);
        }

        string target = StorageKeys.CorruptPrefix + _clock++;
        WriteText(target, raw);
        _slots.Remove(key);
        return Done(1);
    }

    private bool TryList(string key, string list, out JsonObject? doc, out JsonArray? items, out StoreResult? refusal)
    {
        Classified current = Classify(key);
        refusal = current.Refusal;
        doc = null;
        items = null;
        if (refusal is not null)
        {
            return false;
        }

        doc = current.Doc ?? new JsonObject { ["version"] = Version };
        if (QuotaFull)
        {
            // A failed write must leave the cached document unchanged.
            doc = (JsonObject)doc.DeepClone();
        }

        if (doc[list] is not JsonArray array)
        {
            array = [];
            doc[list] = array;
        }

        items = array;
        return true;
    }

    private Classified Classify(string key)
    {
        if (!_slots.TryGetValue(key, out Slot? slot))
        {
            return new Classified(null, null);
        }

        if (slot.Doc is not null)
        {
            return new Classified(slot.Doc, null);
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(slot.Text);
        }
        catch (JsonException)
        {
            return new Classified(null, Fail(StoreErrors.Corrupt));
        }

        if (node is not JsonObject doc || doc["version"] is not JsonValue version || !version.TryGetValue(out int number))
        {
            return new Classified(null, Fail(StoreErrors.Corrupt));
        }

        if (number != Version)
        {
            return new Classified(null, Fail(StoreErrors.NewerVersion));
        }

        if (doc["answers"] is { } answers && answers is not JsonArray
            || doc["items"] is { } items && items is not JsonArray
            || doc["revision"] is { } revision && !(revision is JsonValue r && r.TryGetValue(out long _)))
        {
            return new Classified(null, Fail(StoreErrors.Corrupt));
        }

        // The text stays what was stored until a commit replaces both: ops mutate the node only right before committing.
        _slots[key] = slot with { Doc = doc };
        return new Classified(doc, null);
    }

    private void Commit(string key, JsonObject doc, string list)
    {
        if (((JsonArray)doc[list]!).Count == 0)
        {
            _slots.Remove(key);
            Writes++;
            return;
        }

        if (QuotaFull)
        {
            string text = doc.ToJsonString();
            if (text.Length > (Raw(key)?.Length ?? 0))
            {
                throw new QuotaException();
            }
        }

        _slots[key] = new Slot(null, doc);
        Writes++;
    }

    private void WriteText(string key, string text)
    {
        if (QuotaFull && text.Length > (Raw(key)?.Length ?? 0))
        {
            throw new QuotaException();
        }

        _slots[key] = new Slot(text, null);
        Writes++;
    }

    private int Merge(JsonArray items, JsonArray added, string id)
    {
        int appended = 0;
        Dictionary<string, int> index = _indexes.GetValue(items, list => IndexOf(list, id));

        foreach (JsonNode? entry in added.ToList())
        {
            JsonNode copy = entry!.DeepClone();
            string value = IdOf(copy, id)!;
            if (index.TryGetValue(value, out int at))
            {
                items[at] = copy;
            }
            else
            {
                index[value] = items.Count;
                items.Add(copy);
                appended++;
            }
        }

        return appended;
    }

    private static Dictionary<string, int> IndexOf(JsonArray items, string id)
    {
        Dictionary<string, int> index = new(StringComparer.Ordinal);
        for (int i = 0; i < items.Count; i++)
        {
            if (IdOf(items[i], id) is string value)
            {
                index.TryAdd(value, i);
            }
        }

        return index;
    }

    private int RemoveWhere(JsonArray items, Func<JsonNode?, bool> predicate)
    {
        _indexes.Remove(items);
        int removed = 0;
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (predicate(items[i]))
            {
                items.RemoveAt(i);
                removed++;
            }
        }

        return removed;
    }

    private static string? IdOf(JsonNode? entry, string id) =>
        entry is JsonObject value && value[id] is JsonValue text && text.TryGetValue(out string? result) ? result : null;

    private static StoreResult Done(int count) => new(true, true, null, null, count);

    private static StoreResult Fail(string error) => new(false, false, error, null, 0);

    private sealed record Slot(string? RawText, JsonObject? Doc)
    {
        public string Text => RawText ?? Doc!.ToJsonString();
    }

    private sealed record Classified(JsonObject? Doc, StoreResult? Refusal);

    private sealed class QuotaException : Exception;
}
