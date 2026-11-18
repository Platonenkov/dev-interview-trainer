using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trainer.Web.Client.Storage;

/// <summary>
/// One mutation for <c>window.ditStore.mutate</c> (<c>outbox.js</c>). The script derives the keys to lock from
/// <see cref="Key"/> and <see cref="To"/>; create ops through the factory methods, which fill exactly the members each
/// op reads.
/// </summary>
/// <param name="Op">The op name: <c>append</c>, <c>remove</c>, <c>move</c>, <c>summaryAdd</c>, <c>summaryRemove</c>,
/// <c>historyAdd</c>, <c>putIfRevision</c>, <c>put</c>, <c>delete</c> or <c>quarantine</c>.</param>
/// <param name="Value">Entries to append, the summary item to add, the document to put, or for <c>move</c> the fields
/// set on every moved entry.</param>
/// <param name="Shape"><c>outbox</c> (default) or <c>summaries</c>: which list and id field a <c>move</c> works on.</param>
/// <param name="SessionId">For <c>delete</c>: delete only when the stored document belongs to this mock session. For
/// <c>putIfRevision</c>: a stored document of another mock session is a conflict whatever its revision.</param>
public sealed record StoreOp(
    string Op,
    string Key,
    string? To = null,
    IReadOnlyList<string>? Ids = null,
    JsonElement? Value = null,
    long? ExpectedRevision = null,
    string? Shape = null,
    string? SessionId = null)
{
    /// <summary>Merges answers into an outbox by <c>clientAnswerId</c>.</summary>
    public static StoreOp Append(string key, JsonElement entries) => new("append", key, Value: entries);

    /// <summary>
    /// Removes the outbox answers with these ids; other entries, including ones added meanwhile and entries without a
    /// string <c>clientAnswerId</c>, stay.
    /// </summary>
    public static StoreOp Remove(string key, IReadOnlyList<string> ids) => new("remove", key, Ids: ids);

    /// <summary>
    /// Moves entries (all, or only <paramref name="ids"/>) destination first: merge into <paramref name="to"/>, then remove
    /// from <paramref name="from"/>. A crash between the writes leaves them in both keys; the next move removes them.
    /// </summary>
    public static StoreOp Move(string from, string to, IReadOnlyList<string>? ids = null, string? shape = null, JsonElement? set = null) =>
        new("move", from, To: to, Ids: ids, Value: set, Shape: shape);

    /// <summary>Merges one summary item into a summary queue by <c>mockSessionId</c>.</summary>
    public static StoreOp SummaryAdd(string key, JsonElement item) => new("summaryAdd", key, Value: item);

    /// <summary>Removes one summary item by <c>mockSessionId</c>.</summary>
    public static StoreOp SummaryRemove(string key, string mockSessionId) => new("summaryRemove", key, Ids: [mockSessionId]);

    /// <summary>
    /// Merges one finished mock (a <c>StoredMockHistoryItem</c>) into the history by <c>id</c> and keeps only the newest
    /// <see cref="Trainer.Contracts.Storage.StoredMockHistory.MaxItems"/> of each stack, in one step under the key's lock.
    /// </summary>
    public static StoreOp HistoryAdd(string key, JsonElement item) => new("historyAdd", key, Value: item);

    /// <summary>
    /// Writes the document only when the stored <c>revision</c> (0 when absent) equals <paramref name="expectedRevision"/>
    /// and, when <paramref name="sessionId"/> is given, a stored document belongs to that mock session.
    /// </summary>
    public static StoreOp PutIfRevision(string key, JsonElement document, long expectedRevision, string? sessionId = null) =>
        new("putIfRevision", key, Value: document, ExpectedRevision: expectedRevision, SessionId: sessionId);

    /// <summary>Last writer wins; <c>outbox.js</c> allows it only for preferences and the write-once mock questions key.</summary>
    public static StoreOp Put(string key, JsonElement document) => new("put", key, Value: document);

    /// <summary>Deletes an active-mock key, only if it still belongs to <paramref name="sessionId"/> when one is given.</summary>
    public static StoreOp Delete(string key, string? sessionId = null) => new("delete", key, SessionId: sessionId);

    /// <summary>Copies an unparseable value to <see cref="StorageKeys.CorruptPrefix"/> and removes it only if the copy succeeded.</summary>
    public static StoreOp Quarantine(string key) => new("quarantine", key);
}

/// <summary>What <c>outbox.js</c> reports for one op.</summary>
/// <param name="Ok">The op ran to completion and its result is in storage.</param>
/// <param name="Persisted">The new data reached storage. Differs from <paramref name="Ok"/> only for a <c>move</c> whose
/// destination was written but whose source removal failed: the entries are safe in both keys.</param>
/// <param name="Error">One of <see cref="StoreErrors"/> when <paramref name="Ok"/> is false.</param>
/// <param name="Revision">For <c>putIfRevision</c>: the stored revision after the op, or the conflicting one.</param>
/// <param name="Count">Entries appended, removed or moved.</param>
/// <param name="Fingerprint">For an outbox <c>append</c>: the fingerprint of the log after it (see <see cref="IBrowserStore.FingerprintAsync"/>).</param>
/// <param name="Previous">For an outbox <c>append</c>: the fingerprint of the log just before it, so a cache that holds the
/// state before can tell that nothing else was written in between.</param>
/// <param name="Added">For an outbox <c>append</c>: how many of the entries were new; the others replaced entries with the same id.</param>
public sealed record StoreResult(bool Ok, bool Persisted, string? Error, long? Revision, int Count = 0, string? Fingerprint = null, string? Previous = null, int Added = 0)
{
    public static StoreResult Failed(string error) => new(false, false, error, null);
}

/// <summary>The error codes of <see cref="StoreResult.Error"/>.</summary>
public static class StoreErrors
{
    /// <summary><c>navigator.locks</c> is missing: nothing is written, the client keeps data in memory only.</summary>
    public const string NoLocks = "no-locks";

    /// <summary><c>localStorage</c> is missing or blocked.</summary>
    public const string NoStorage = "no-storage";

    /// <summary>The browser's storage quota is exhausted; the stored value is unchanged.</summary>
    public const string Quota = "quota";

    /// <summary>Any other write failure; the stored value is unchanged.</summary>
    public const string WriteFailed = "write-failed";

    /// <summary>The stored value is not a readable document; quarantine it, then retry.</summary>
    public const string Corrupt = "corrupt";

    /// <summary>The stored document has a version this app does not know (a newer app wrote it); it is never touched.</summary>
    public const string NewerVersion = "newer-version";

    /// <summary><c>putIfRevision</c> found another revision: another tab wrote it.</summary>
    public const string Revision = "revision";

    /// <summary>The op is malformed or not allowed on that key.</summary>
    public const string BadOp = "bad-op";

    /// <summary><c>outbox.js</c> is not loaded or threw.</summary>
    public const string Unavailable = "unavailable";
}

/// <summary>A raw read that says whether storage could be read at all.</summary>
/// <param name="Readable">False when reading threw (blocked or broken <c>localStorage</c>, no <c>outbox.js</c>).</param>
/// <param name="Value">The stored string; <c>null</c> for an absent key or an unreadable store.</param>
public readonly record struct StoredRead(bool Readable, string? Value);

/// <summary>The outcome of saving active-mock data.</summary>
public enum SaveResult
{
    Saved,

    /// <summary>Another tab saved a newer revision of the same mock first.</summary>
    OtherWriter,

    Failed,
}

/// <summary>Serializer metadata for the <c>outbox.js</c> op and result (the client is trimmed).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StoreOp))]
[JsonSerializable(typeof(StoreResult))]
internal sealed partial class StorageJsonContext : JsonSerializerContext;
