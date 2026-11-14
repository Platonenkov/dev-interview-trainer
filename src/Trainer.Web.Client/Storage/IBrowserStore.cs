namespace Trainer.Web.Client.Storage;

/// <summary>
/// The browser's <c>localStorage</c> as <c>outbox.js</c> exposes it: every write is one op that runs under Web Locks on
/// every key it touches. The methods take no cancellation token on purpose: an op that already holds its locks runs to
/// the end in one JavaScript task, so cancelling the wait could only misreport a write that happened. The lock wait has
/// no timeout either: every lock holder runs a synchronous body and releases at once, so a lock is only ever held for
/// one op; a hung holder would need a frozen tab, and the browser releases a closed tab's locks.
/// </summary>
public interface IBrowserStore
{
    ValueTask<StoreResult> MutateAsync(StoreOp op);

    /// <summary>The raw stored string, or <c>null</c> when the key is absent or storage cannot be read.</summary>
    ValueTask<string?> ReadAsync(string key);

    /// <summary>
    /// Like <see cref="ReadAsync"/>, but tells an absent key (<see cref="StoredRead.Readable"/> true, no value) from storage
    /// that could not be read (<see cref="StoredRead.Readable"/> false), for callers that must not take a failed read for
    /// "nothing is stored". By default every read counts as readable.
    /// </summary>
    async ValueTask<StoredRead> ReadCheckedAsync(string key) => new(true, await ReadAsync(key));
}
