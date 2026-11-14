using System.Net;
using Trainer.Contracts;
using Trainer.Contracts.Storage;
using Trainer.Web.Client.Api;
using Trainer.Web.Client.Storage;

namespace Trainer.Web.Client.Sync;

/// <summary>
/// Sends one signed-in owner's outbox and pending mock summaries to the server (spec 8.8). The invariant: an answer
/// leaves the outbox only when its id is in <c>Accepted</c> or <c>Rejected</c> of a 200 response, and a summary leaves
/// the pending list only on a 204 (or moves to the failed list on a coded refusal); every other outcome keeps it and
/// backs off (5 s, doubling to 5 min). The owner is bound once by <see cref="Start"/>: only that owner's keys are read
/// and every write names it. A 409 <c>owner-mismatch</c>, a 401 or another tab publishing another account
/// (<see cref="AccountWatcher"/>) stops syncing for good with everything kept. The outbox is shared with the island and
/// other tabs, so every attempt reads it afresh and removes only the acknowledged ids.
/// </summary>
public sealed class OutboxSyncService : IDisposable
{
    /// <summary>Under the server's 500 limit and small enough to retry cheaply.</summary>
    public const int ChunkSize = 200;

    public static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

    private readonly OutboxStore _outbox;
    private readonly SummaryStore _summaries;
    private readonly TrainerApiClient _api;
    private readonly TimeProvider _time;
    private readonly AccountWatcher _accounts;

    private long? _owner;
    private bool _stopped;
    private int _chunk = ChunkSize;
    private TimeSpan _delay = FirstRetryDelay;
    private ITimer? _retry;
    private Task _running = Task.CompletedTask;
    private bool _again;

    // Cancelled by Stop: no further request (not even an antiforgery retry) leaves, and the fetch in flight is aborted in the
    // browser, so a batch the server already stored may go unacknowledged here. Nothing is lost: its ids stay in the owner's
    // outbox and are sent again later, when the server deduplicates them.
    private readonly CancellationTokenSource _stopping = new();

    /// <param name="accounts">The page's account watcher: a different account published in another tab stops syncing
    /// here, whether or not a component shows the notice.</param>
    public OutboxSyncService(OutboxStore outbox, SummaryStore summaries, TrainerApiClient api, TimeProvider time, AccountWatcher accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        _outbox = outbox;
        _summaries = summaries;
        _api = api;
        _time = time;
        _accounts = accounts;
        _accounts.Changed += OnAccountChanged;
    }

    private enum Outcome
    {
        Done,

        /// <summary>The server answered and refused or could not take something; other requests may still succeed.</summary>
        Retry,

        /// <summary>No response, 429 or 5xx: further requests in this attempt would only fail the same way.</summary>
        Unreachable,

        Stopped,
    }

    /// <summary>Raised when <see cref="Pending"/> changes or syncing stops.</summary>
    public event Action? Changed;

    /// <summary>The server answered 409 <c>owner-mismatch</c>: another account is signed in now. Syncing has stopped.</summary>
    public event Action? AccountMismatch;

    /// <summary>The server answered 401: nobody is signed in now. Syncing has stopped.</summary>
    public event Action? SignedOut;

    /// <summary>Answers of the bound owner not yet acknowledged, as of the last attempt.</summary>
    public int Pending { get; private set; }

    public bool IsStopped => _stopped;

    /// <summary>The server answered 409 <c>owner-mismatch</c> at some point; stays true, so a late subscriber still knows.</summary>
    public bool HasAccountMismatch { get; private set; }

    /// <summary>The server answered 401 at some point; stays true, so a late subscriber still knows.</summary>
    public bool IsSignedOut { get; private set; }

    /// <summary>The attempt in progress, or a completed task when idle (tests wait on it).</summary>
    internal Task CurrentAttempt => _running;

    /// <summary>Binds the owner (a Telegram user id) for the life of this service and starts a flush.</summary>
    /// <exception cref="InvalidOperationException">Another owner is already bound.</exception>
    public void Start(long owner)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(owner);
        if (_owner is { } bound && bound != owner)
        {
            throw new InvalidOperationException("The sync service is already bound to another owner.");
        }

        _owner = owner;
        if (_accounts.ChangedTo is not null)
        {
            Stop();
            return;
        }

        RequestFlush();
    }

    /// <summary>Flushes soon: now, unless an attempt is waiting out its back-off (then the timer flushes).</summary>
    public void RequestFlush()
    {
        if (_owner is null || _stopped || _retry is not null)
        {
            return;
        }

        _ = RunAsync();
    }

    /// <summary>
    /// Stops syncing for good; nothing is removed, and no further request leaves, even mid-attempt. A request in flight is
    /// aborted, so its answers stay in the outbox until a later page sends them again.
    /// </summary>
    public void Stop()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        _stopping.Cancel();
        CancelRetry();
        Changed?.Invoke();
    }

    /// <summary>
    /// Flush-before-fetch: tries now, skipping any back-off, and waits for that attempt up to <paramref name="maxWait"/>.
    /// True when the owner's outbox and pending summaries are empty afterwards (always true with no owner bound); false
    /// when they are not, cannot be read, or the attempt outlives <paramref name="maxWait"/>. The only exception is
    /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled (the caller went away); the attempt
    /// itself is not cancelled by it.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<bool> FlushAsync(TimeSpan maxWait, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_owner is not { } owner)
        {
            return true;
        }

        if (!_stopped)
        {
            CancelRetry();
            try
            {
                await RunAsync().WaitAsync(maxWait, _time, ct);
            }
            catch (TimeoutException)
            {
                // The attempt goes on in the background; the caller continues with what is synced so far.
            }
        }

        try
        {
            int answers = (await _outbox.ReadAsync(StorageKeys.UserOutbox(owner))).Count;
            SetPending(answers);
            return answers == 0 && (await _summaries.ReadPendingAsync(owner)).Count == 0;
        }
#pragma warning disable CA1031 // The caller only needs to know the data is not confirmed synced; it never handles a storage failure.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    public void Dispose()
    {
        _accounts.Changed -= OnAccountChanged;
        _stopped = true;
        // Cancelled, not disposed: an attempt still finishing may read its token, and a CancellationTokenSource without
        // a timer holds nothing to release.
        _stopping.Cancel();
        CancelRetry();
    }

    private void OnAccountChanged(string owner) => Stop();

    private Task RunAsync()
    {
        if (!_running.IsCompleted)
        {
            _again = true;
            return _running;
        }

        _running = LoopAsync();
        return _running;
    }

    private async Task LoopAsync()
    {
        Outcome outcome;
        do
        {
            _again = false;
            try
            {
                outcome = await FlushOnceAsync(_owner!.Value);
            }
#pragma warning disable CA1031 // A storage or interop failure must not end syncing for the page: it is retried like a 5xx.
            catch (Exception)
#pragma warning restore CA1031
            {
                outcome = Outcome.Unreachable;
            }
        }
        while (_again && outcome == Outcome.Done && !_stopped);

        if (outcome == Outcome.Done)
        {
            _delay = FirstRetryDelay;
            _chunk = ChunkSize;
        }
        else if (!_stopped)
        {
            ScheduleRetry();
        }
    }

    private async Task<Outcome> FlushOnceAsync(long owner)
    {
        Outcome answers = await SendAnswersAsync(owner);
        if (answers is Outcome.Stopped or Outcome.Unreachable)
        {
            // Summaries would meet the same outage and spend the per-user write limit for nothing.
            return answers;
        }

        Outcome summaries = await SendSummariesAsync(owner);
        return summaries == Outcome.Done ? answers : summaries;
    }

    private async Task<Outcome> SendAnswersAsync(long owner)
    {
        string key = StorageKeys.UserOutbox(owner);
        IReadOnlyList<AnswerDto> answers = await _outbox.ReadAsync(key);
        SetPending(answers.Count);
        Outcome outcome = Outcome.Done;
        int start = 0;
        while (start < answers.Count)
        {
            if (_stopped)
            {
                return Outcome.Stopped;
            }

            int size = Math.Min(_chunk, answers.Count - start);
            AnswerDto[] chunk = [.. answers.Skip(start).Take(size)];
            ApiResult<AnswerBatchResponse> result = await _api.PostAnswersAsync(owner, new AnswerBatch(AnswerBatch.CurrentVersion, chunk), _stopping.Token);
            if (StopsSync(result.Status, result.ErrorCode))
            {
                return Outcome.Stopped;
            }

            if (result is not { Status: (int)HttpStatusCode.OK, Value: { } acknowledgement })
            {
                if (IsTooLarge(result.Status, result.ErrorCode))
                {
                    _chunk = Math.Max(1, size / 2);
                }

                outcome = IsUnreachable(result.Status) ? Outcome.Unreachable : Outcome.Retry;
                break;
            }

            HashSet<string> sent = [.. chunk.Select(a => a.ClientAnswerId)];
            List<string> acknowledged =
            [
                .. (acknowledgement.Accepted ?? [])
                    .Concat((acknowledgement.Rejected ?? []).Where(r => r is not null).Select(r => r.ClientAnswerId))
                    .Where(id => id is not null && sent.Remove(id)),
            ];
            if (acknowledged.Count > 0)
            {
                await _outbox.RemoveAsync(key, acknowledged);
            }

            if (sent.Count > 0)
            {
                // The server must name every id it was sent; one it did not name is not acknowledged.
                outcome = Outcome.Retry;
            }

            start += size;
        }

        SetPending((await _outbox.ReadAsync(key)).Count);
        return _stopped ? Outcome.Stopped : outcome;
    }

    private async Task<Outcome> SendSummariesAsync(long owner)
    {
        Outcome outcome = Outcome.Done;
        foreach (StoredMockSummary item in await _summaries.ReadPendingAsync(owner))
        {
            if (_stopped)
            {
                return Outcome.Stopped;
            }

            ApiResult<bool> result = await _api.PutMockSummaryAsync(owner, item.MockSessionId, item.Summary, _stopping.Token);
            if (StopsSync(result.Status, result.ErrorCode))
            {
                return Outcome.Stopped;
            }

            if (result.Value)
            {
                // Stored on the server but still listed here: the next attempt sends it again (the PUT is idempotent).
                if (!(await _summaries.RemovePendingAsync(owner, item.MockSessionId)).Ok)
                {
                    outcome = Outcome.Retry;
                }
            }
            else if (IsRefused(result.Status, result.ErrorCode))
            {
                // Retrying cannot change the answer; the profile lists it with "Could not be saved" and Discard.
                if (!(await _summaries.MoveToFailedAsync(owner, item.MockSessionId, result.ErrorCode!)).Ok)
                {
                    outcome = Outcome.Retry;
                }
            }
            else
            {
                outcome = Outcome.Retry;
            }
        }

        return outcome;
    }

    private bool StopsSync(int status, string? code)
    {
        if (status == (int)HttpStatusCode.Conflict && string.Equals(code, ApiErrorCodes.OwnerMismatch, StringComparison.Ordinal))
        {
            HasAccountMismatch = true;
            Stop();
            AccountMismatch?.Invoke();
            return true;
        }

        if (status == (int)HttpStatusCode.Unauthorized)
        {
            IsSignedOut = true;
            Stop();
            SignedOut?.Invoke();
            return true;
        }

        return false;
    }

    private static bool IsUnreachable(int status) => status is 0 or (int)HttpStatusCode.TooManyRequests or >= 500;

    private static bool IsTooLarge(int status, string? code) =>
        status == (int)HttpStatusCode.RequestEntityTooLarge
        || (status == (int)HttpStatusCode.BadRequest && string.Equals(code, ApiErrorCodes.BatchTooLarge, StringComparison.Ordinal));

    // Only the endpoint's own coded answer is final. A 404 coded not-found comes from this app (a proxy's 404 has no
    // ApiErrorDto); since every deployed version maps PUT /api/mock-sessions/{id}, it means the id is unknown or not
    // this user's. The failed list keeps the summary, so even a wrong call loses nothing.
    private static bool IsRefused(int status, string? code) =>
        (status == (int)HttpStatusCode.NotFound && string.Equals(code, ApiErrorCodes.NotFound, StringComparison.Ordinal))
        || (status == (int)HttpStatusCode.BadRequest && string.Equals(code, ApiErrorCodes.Validation, StringComparison.Ordinal));

    private void ScheduleRetry()
    {
        CancelRetry();
        TimeSpan delay = _delay;
        _delay = delay * 2 > MaxRetryDelay ? MaxRetryDelay : delay * 2;
        _retry = _time.CreateTimer(_ => OnRetryDue(), null, delay, Timeout.InfiniteTimeSpan);
    }

    private void OnRetryDue()
    {
        CancelRetry();
        RequestFlush();
    }

    private void CancelRetry()
    {
        _retry?.Dispose();
        _retry = null;
    }

    private void SetPending(int count)
    {
        if (Pending != count)
        {
            Pending = count;
            Changed?.Invoke();
        }
    }
}
