using System.Buffers;
using System.Text.Json;
using Trainer.Contracts;
using Trainer.Contracts.Storage;
using Trainer.Domain.Ids;

namespace Trainer.Web.Client.Storage;

/// <summary>
/// The mock summaries a signed-in user still has to send (<c>PUT /api/mock-sessions/{id}</c>) and the ones the server
/// refused, so the user can retry or discard them. Every change merges or removes one item by mock session id under
/// the key's lock; the queue is never rewritten whole, so two tabs finishing mocks at once both keep their summary.
/// A summary that storage does not take (no Web Locks, no <c>localStorage</c>, quota) stays in this tab's memory, like
/// an unsaved outbox answer: reads return it, so the sync service still sends it, and it is gone with the page.
/// </summary>
public sealed class SummaryStore(IBrowserStore store)
{
    private const string ItemsMember = "items";

    private static readonly StoreResult Succeeded = new(true, true, null, null);

    private readonly NewerVersionKeys _newer = new();

    // Per storage key, by mock session id: the items held only in this tab's memory.
    private readonly Dictionary<string, OrderedDictionary<string, StoredMockSummary>> _held = new(StringComparer.Ordinal);

    /// <summary>Raised when <see cref="HasNewerVersion"/> or <see cref="UnsavedCount"/> changes.</summary>
    public event Action? Changed;

    /// <summary>A summary list was written by a newer app version; it is left untouched until the page is reloaded.</summary>
    public bool HasNewerVersion => _newer.Any;

    /// <summary>Summaries held only in this tab's memory, pending or failed.</summary>
    public int UnsavedCount => _held.Values.Sum(items => items.Count);

    /// <summary>
    /// Adds a summary to the pending queue. When storage does not take it (<see cref="StoreResult.Persisted"/> false), it
    /// is held in memory and still read and sent; a later add that reaches storage replaces the held copy.
    /// </summary>
    public async ValueTask<StoreResult> AddPendingAsync(long owner, string mockSessionId, MockSummaryDto summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        string key = StorageKeys.PendingSummaries(owner);
        StoredMockSummary item = new(mockSessionId, summary, null);
        StoreResult result = await MutateAsync(StoreOp.SummaryAdd(key, Serialize(item)));
        if (result.Persisted)
        {
            Unhold(key, mockSessionId);
        }
        else
        {
            Hold(key, item);
        }

        return result;
    }

    /// <summary>
    /// Holds a summary in the pending queue in memory only, never in storage: for a finished mock whose answers are
    /// themselves only in memory, since a stored summary must never count answers that a reload would lose.
    /// </summary>
    public void HoldPending(long owner, string mockSessionId, MockSummaryDto summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        Hold(StorageKeys.PendingSummaries(owner), new StoredMockSummary(mockSessionId, summary, null));
    }

    public ValueTask<IReadOnlyList<StoredMockSummary>> ReadPendingAsync(long owner) => ReadAsync(StorageKeys.PendingSummaries(owner));

    public ValueTask<StoreResult> RemovePendingAsync(long owner, string mockSessionId) =>
        RemoveAsync(StorageKeys.PendingSummaries(owner), mockSessionId);

    /// <summary>
    /// Moves a refused summary to the failed queue with the server's reason, in one op under both keys' locks. A summary
    /// held in memory is added to the failed queue instead (and held there when storage refuses it too).
    /// </summary>
    public async ValueTask<StoreResult> MoveToFailedAsync(long owner, string mockSessionId, string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        string pendingKey = StorageKeys.PendingSummaries(owner);
        string failedKey = StorageKeys.FailedSummaries(owner);
        if (Unhold(pendingKey, mockSessionId) is not { } held)
        {
            return await MutateAsync(StoreOp.Move(pendingKey, failedKey, [mockSessionId], "summaries", ReasonPatch(reason)));
        }

        StoredMockSummary failed = held with { Reason = reason };
        if (!(await MutateAsync(StoreOp.SummaryAdd(failedKey, Serialize(failed)))).Persisted)
        {
            Hold(failedKey, failed);
        }

        // An older copy that did reach the pending queue must not be sent again.
        await MutateAsync(StoreOp.SummaryRemove(pendingKey, mockSessionId));
        return Succeeded;
    }

    public ValueTask<IReadOnlyList<StoredMockSummary>> ReadFailedAsync(long owner) => ReadAsync(StorageKeys.FailedSummaries(owner));

    public ValueTask<StoreResult> DiscardFailedAsync(long owner, string mockSessionId) =>
        RemoveAsync(StorageKeys.FailedSummaries(owner), mockSessionId);

    // A summary held in memory is gone once it leaves memory: storage, which this tab cannot reach, cannot refuse that.
    private async ValueTask<StoreResult> RemoveAsync(string key, string mockSessionId)
    {
        bool held = Unhold(key, mockSessionId) is not null;
        StoreResult result = await MutateAsync(StoreOp.SummaryRemove(key, mockSessionId));
        return result.Ok || !held || !IsUnavailable(result.Error) ? result : Succeeded;
    }

    private static bool IsUnavailable(string? error) => error is StoreErrors.NoLocks or StoreErrors.NoStorage or StoreErrors.Unavailable;

    private void Hold(string key, StoredMockSummary item)
    {
        if (!_held.TryGetValue(key, out OrderedDictionary<string, StoredMockSummary>? items))
        {
            items = new OrderedDictionary<string, StoredMockSummary>(StringComparer.Ordinal);
            _held[key] = items;
        }

        items[item.MockSessionId] = item;
        Changed?.Invoke();
    }

    private StoredMockSummary? Unhold(string key, string mockSessionId)
    {
        if (!_held.TryGetValue(key, out OrderedDictionary<string, StoredMockSummary>? items) || !items.Remove(mockSessionId, out StoredMockSummary? item))
        {
            return null;
        }

        Changed?.Invoke();
        return item;
    }

    private static JsonElement Serialize(StoredMockSummary item) =>
        JsonSerializer.SerializeToElement(item, TrainerJsonContext.Default.StoredMockSummary);

    private async ValueTask<StoreResult> MutateAsync(StoreOp op)
    {
        StoreResult result = await store.MutateWithQuarantineAsync(op);
        bool changed = _newer.Note(op.Key, result);
        if (op.To is not null)
        {
            // A refused move does not say which key is newer; a later read of either key settles it.
            changed |= _newer.Note(op.To, result);
        }

        RaiseIf(changed);
        return result;
    }

    private async ValueTask<IReadOnlyList<StoredMockSummary>> ReadAsync(string key)
    {
        StoredDocument document = await store.ReadDocumentAsync(key);
        RaiseIf(_newer.Note(key, document.Kind));
        List<StoredMockSummary> items = [.. document.Items(ItemsMember).Select(ReadItem).OfType<StoredMockSummary>()];
        if (_held.TryGetValue(key, out OrderedDictionary<string, StoredMockSummary>? held) && held.Count > 0)
        {
            // The held copy wins: it is what this tab tried to write last.
            items.RemoveAll(i => held.ContainsKey(i.MockSessionId));
            items.AddRange(held.Values);
        }

        return items;
    }

    private void RaiseIf(bool changed)
    {
        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private static StoredMockSummary? ReadItem(JsonElement element)
    {
        StoredMockSummary? item;
        try
        {
            item = element.Deserialize(TrainerJsonContext.Default.StoredMockSummary);
        }
        catch (JsonException)
        {
            return null;
        }

        return item is not null && UlidText.IsValid(item.MockSessionId) && item.Summary?.Topics is not null ? item : null;
    }

    private static JsonElement ReasonPatch(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("reason", reason);
            writer.WriteEndObject();
        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
