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

    /// <summary>
    /// Identifies the state of an outbox without reading it: the entry count and a hash of its tail, as <c>outbox.js</c>
    /// computes them natively (an answer log is far too big to read into .NET just to compare it). Null when the key holds
    /// something that is not a readable outbox or when there is no script, so the caller reads the whole log instead.
    /// </summary>
    ValueTask<string?> FingerprintAsync(string key) => ValueTask.FromResult<string?>(null);

    /// <summary>
    /// Calls <paramref name="onChanged"/> whenever another tab changes <paramref name="key"/> (the browser's <c>storage</c>
    /// event, which never fires in the writing tab); disposing the result stops it. Without the script or without storage
    /// nothing is ever called, so a caller treats the event as a courtesy that only makes its cache fresher sooner.
    /// </summary>
    ValueTask<IAsyncDisposable> WatchKeyAsync(string key, Action onChanged) => ValueTask.FromResult<IAsyncDisposable>(NoWatch.Instance);

    private sealed class NoWatch : IAsyncDisposable
    {
        public static readonly NoWatch Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
