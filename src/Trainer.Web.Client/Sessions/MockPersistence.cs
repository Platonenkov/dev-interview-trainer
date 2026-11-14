using Trainer.Contracts;
using Trainer.Contracts.Storage;
using Trainer.Web.Client.Api;
using Trainer.Web.Client.Storage;

namespace Trainer.Web.Client.Sessions;

/// <summary>What one run of <see cref="MockPersistence.HandOffAsync"/> achieved.</summary>
/// <param name="AnswersPersisted">The answers are in the outbox in storage (in an earlier run or this one).</param>
/// <param name="SummaryHandled">The summary is in the pending queue, or the owner is anonymous and has none to queue.</param>
/// <param name="Cleared">Both mock keys were removed: nothing is left to resume.</param>
public sealed record MockHandoff(bool AnswersPersisted, bool SummaryHandled, bool Cleared);

/// <summary>What <see cref="MockPersistence.ReleaseAnonymousAsync"/> found under the anonymous owner.</summary>
public enum AnonymousMockState
{
    /// <summary>No anonymous mock, or one that can be neither graded nor resumed (its questions are gone).</summary>
    None,

    /// <summary>An unfinished anonymous mock that the user has not chosen to end.</summary>
    Unfinished,

    /// <summary>Its answers are in the anonymous outbox (the profile's import offers them) and its keys are removed.</summary>
    HandedOver,

    /// <summary>Storage did not take the hand-over; the mock is kept and the next load tries again.</summary>
    Pending,
}

/// <summary>
/// Keeps the active mock of this tab in <c>localStorage</c> (ruling D): the questions once at start, the small progress
/// document on every change with a revision check, typed text debounced. A revision conflict means another tab now
/// writes this mock: this tab turns read-only for good. The completion hand-over is ordered and resumable, see
/// <see cref="HandOffAsync"/>. Every wait on storage is bounded by <see cref="WriteTimeout"/>, because a Web Lock that is
/// never granted must not freeze the island: on a timeout the write goes on in the background, the data stays in this
/// tab's memory, and <see cref="IsSlow"/> says so until a write completes.
/// </summary>
public sealed class MockPersistence : IDisposable
{
    /// <summary>Typed answers and notes are saved this long after the last keystroke.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    /// <summary>How long the island waits for one storage step before it carries on without it.</summary>
    public static readonly TimeSpan WriteTimeout = SlowStorage.WriteTimeout;

    private readonly ActiveMockStore _mocks;
    private readonly OutboxStore _outbox;
    private readonly SummaryStore _summaries;
    private readonly TimeProvider _time;
    private readonly SlowStorage _slow;
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly Lock _timerGate = new();
    private ITimer? _debounce;
    private MockSession? _pending;
    private bool _detached;
    // The mock this instance started; null for an adopted (resumed) one, whose saves the store's session check guards.
    private string? _activeSessionId;
    private Task _idle = Task.CompletedTask;

    public MockPersistence(ActiveMockStore mocks, OutboxStore outbox, SummaryStore summaries, TimeProvider time)
    {
        _mocks = mocks;
        _outbox = outbox;
        _summaries = summaries;
        _time = time;
        _slow = new SlowStorage(time);
        _slow.Changed += () => Changed?.Invoke();
    }

    /// <summary>Raised when <see cref="IsReadOnly"/> or <see cref="IsSlow"/> changes.</summary>
    public event Action? Changed;

    /// <summary>The revision of the last progress document this tab saved or loaded; 0 when there is none.</summary>
    public long Revision { get; private set; }

    /// <summary>Another tab saved this mock after this tab did: nothing is written from here any more.</summary>
    public bool IsReadOnly { get; private set; }

    /// <summary>A storage step took longer than <see cref="WriteTimeout"/> and is still running.</summary>
    public bool IsSlow => _slow.IsSlow;

    /// <summary>The debounced save in flight, or a completed task (tests wait on it).</summary>
    public Task Idle => _idle;

    /// <summary>
    /// The owner's active mock as saved, or a null session when there is none or it cannot be restored (its questions are
    /// missing or do not match). Loading changes nothing here: only <see cref="Adopt"/> makes this tab continue the mock.
    /// </summary>
    public async Task<(MockSession? Session, MockProgress? Progress)> LoadAsync(string owner, ServerClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        (MockProgress? progress, IReadOnlyList<QuestionDto>? questions) = await _mocks.LoadAsync(owner);
        if (progress is null)
        {
            return (null, null);
        }

        if (questions is null)
        {
            return (null, progress);
        }

        try
        {
            return (MockSession.Restore(progress, questions, clock), progress);
        }
        catch (ArgumentException)
        {
            return (null, progress);
        }
    }

    /// <summary>This tab continues the loaded mock: its saves follow on the loaded revision.</summary>
    public void Adopt(MockProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        Revision = progress.Revision;
        _activeSessionId = null;
        _detached = false;
    }

    /// <summary>
    /// Saves a new mock: its questions once, then its first progress document. A stored mock is never deleted while it
    /// still holds something not handed over (P24): a finished one is handed over first, from the stored document, and
    /// removed only once its answers and summary are durable; an unfinished one that can be resumed is left alone. Only
    /// an unfinished mock whose questions are gone (nothing can be graded or resumed) is replaced.
    /// </summary>
    /// <param name="telegramUserId">The signed-in owner, or null for an anonymous one (for a leftover hand-over).</param>
    /// <returns>False when a stored mock must be resumed or finished first, or another tab started a mock at the same
    /// moment: the new mock is then detached and never writes. True otherwise, including when storage is unavailable
    /// (the mock then runs in memory).</returns>
    /// <remarks>
    /// Unlike the other steps this one is not cut short at <see cref="WriteTimeout"/>: a start taken as done while the
    /// release of a stored mock still runs could let the new mock overwrite it. Past the bound <see cref="IsSlow"/> says
    /// so and the caller keeps waiting (the island keeps Start blocked).
    /// </remarks>
    public async Task<bool> StartAsync(MockSession session, long? telegramUserId)
    {
        ArgumentNullException.ThrowIfNull(session);
        Task<bool> start = SerializedAsync(() => StartCoreAsync(session, telegramUserId));
        _ = Bounded(start, false);
        return await start;
    }

    /// <summary>Saves the progress now (a pick, a move, a phase change, a grade); a pending debounced save is folded in.</summary>
    /// <returns>True when the document reached storage.</returns>
    public Task<bool> SaveAsync(MockSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        CancelDebounce();
        return Bounded(SerializedAsync(() => SaveCoreAsync(session)), false);
    }

    /// <summary>Saves after <see cref="Debounce"/> without further calls (typed text, notes).</summary>
    public void ScheduleSave(MockSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_timerGate)
        {
            _pending = session;
            _debounce?.Dispose();
            _debounce = _time.CreateTimer(_ => _idle = FlushAsync(), null, Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Runs a pending debounced save at once (the page is being hidden or closed).</summary>
    public Task FlushAsync()
    {
        MockSession? pending;
        lock (_timerGate)
        {
            pending = _pending;
        }

        return pending is null ? Task.CompletedTask : SaveAsync(pending);
    }

    /// <summary>
    /// Hands a finished mock over, in order, repeating only the steps a reload interrupted: (1) <see cref="MockSession.Complete"/>
    /// stores the answers and the summary in the progress document; (2) the answers go to the owner's outbox, then
    /// <c>AnswersQueued</c> is saved; (3) a signed-in owner's summary goes to the pending queue, then <c>SummaryQueued</c>
    /// is saved (an anonymous summary is only shown); (4) only when (2) and (3) reached storage are both mock keys removed.
    /// When (2) leaves the answers in this tab's memory, a signed-in owner's summary is held in memory beside them, so the
    /// sync service sends both.
    /// The ids are the stored ones, so a repeated step is deduplicated by the outbox and the queue. A read-only tab hands
    /// nothing over: the other tab owns the mock.
    /// </summary>
    /// <param name="telegramUserId">The signed-in owner, or null for an anonymous one.</param>
    public async Task<MockHandoff> HandOffAsync(MockSession session, long? telegramUserId)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (IsReadOnly)
        {
            return new MockHandoff(false, false, false);
        }

        MockCompletionState completion = session.Complete();
        await SaveAsync(session);
        if (IsReadOnly)
        {
            // Another tab saved this mock after this one: that tab owns the hand-over now.
            return new MockHandoff(false, false, false);
        }

        bool answers = completion.AnswersQueued;
        if (!answers)
        {
            answers = await Bounded(_outbox.AddAllAsync(OutboxKey(telegramUserId), completion.Answers).AsTask(), false);
            if (answers)
            {
                session.MarkAnswersQueued();
                await SaveAsync(session);
            }
        }

        // Ordered: the summary is queued only after the answers it counts are durable.
        if (!answers || IsReadOnly)
        {
            if (!answers && !IsReadOnly && telegramUserId is { } signedIn)
            {
                // The answers wait in this tab's memory and the sync service sends them from there; the summary waits
                // beside them in memory, never in storage, so a reload cannot leave a summary without its answers.
                _summaries.HoldPending(signedIn, session.Context.SessionId, completion.Summary);
            }

            return new MockHandoff(answers && !IsReadOnly, false, false);
        }

        bool summary = session.Completion!.SummaryQueued;
        if (!summary)
        {
            summary = telegramUserId is not { } owner
                || (await Bounded(
                    _summaries.AddPendingAsync(owner, session.Context.SessionId, completion.Summary).AsTask(),
                    StoreResult.Failed(StoreErrors.Unavailable))).Persisted;
            if (summary)
            {
                session.MarkSummaryQueued();
                await SaveAsync(session);
            }
        }

        bool cleared = answers && summary && !IsReadOnly
            && await Bounded(SerializedAsync(() => ClearCoreAsync(session)), false);
        return new MockHandoff(answers, summary, cleared);
    }

    /// <summary>
    /// A signed-in page looks for a mock left under the anonymous owner (begun before sign-in): its answers would otherwise
    /// sit inside its progress document, where the anonymous import cannot see them. A finished one is handed over to
    /// the anonymous outbox at once, without a summary (an anonymous summary is only shown). An unfinished one is only
    /// reported unless <paramref name="finish"/> says the user chose to end it: then unanswered questions stay unanswered,
    /// open ones are skipped, and it is handed over the same way. The mock's keys go only once its answers are stored.
    /// </summary>
    public Task<AnonymousMockState> ReleaseAnonymousAsync(ServerClock clock, bool finish)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return Bounded(SerializedAsync(() => ReleaseAnonymousCoreAsync(clock, finish)), AnonymousMockState.Pending);
    }

    public void Dispose()
    {
        lock (_timerGate)
        {
            _pending = null;
            _debounce?.Dispose();
            _debounce = null;
        }
    }

    private async Task<AnonymousMockState> ReleaseAnonymousCoreAsync(ServerClock clock, bool finish)
    {
        (MockSession? session, MockProgress? progress) = await LoadAsync(StorageKeys.AnonymousOwner, clock);
        if (progress is null)
        {
            return AnonymousMockState.None;
        }

        MockProgress stored = progress;
        if (progress.Completion is null)
        {
            if (session is null)
            {
                // Nothing can be graded; the anonymous page replaces it at its next start.
                return AnonymousMockState.None;
            }

            if (!finish)
            {
                return AnonymousMockState.Unfinished;
            }

            session.Finish();
            foreach (QuestionAttempt pending in session.PendingAssessment)
            {
                session.SkipAssessment(IndexOf(session, pending));
            }

            session.Complete();
            // The completion is written by the first flag save below, over the stored revision.
            stored = session.Snapshot(progress.Revision);
        }

        IReadOnlyList<QuestionDto>? questions = session is null ? null : [.. session.Attempts.Select(a => a.Question)];
        return await ReleaseStoredAsync(stored, questions, telegramUserId: null)
            ? AnonymousMockState.HandedOver
            : AnonymousMockState.Pending;
    }

    private static int IndexOf(MockSession session, QuestionAttempt attempt)
    {
        for (int i = 0; i < session.Attempts.Count; i++)
        {
            if (ReferenceEquals(session.Attempts[i], attempt))
            {
                return i;
            }
        }

        throw new ArgumentException("The attempt is not part of the mock.", nameof(attempt));
    }

    private async Task<bool> StartCoreAsync(MockSession session, long? telegramUserId)
    {
        string owner = session.Context.Owner;
        string sessionId = session.Context.SessionId;
        // Detached until the new mock owns the key: on any refusal below it must never write.
        _detached = true;
        _activeSessionId = sessionId;
        IsReadOnly = false;
        (MockProgress? stored, IReadOnlyList<QuestionDto>? storedQuestions) = await _mocks.LoadAsync(owner);
        if (stored is not null && !await ReleaseStoredAsync(stored, storedQuestions, telegramUserId))
        {
            return false;
        }

        Revision = 0;
        IReadOnlyList<QuestionDto> questions = [.. session.Attempts.Select(a => a.Question)];
        if (await _mocks.SaveQuestionsAsync(owner, sessionId, questions) != SaveResult.Saved)
        {
            // Progress without its questions could never be resumed; the mock stays detached and runs in memory only.
            return true;
        }

        MockProgress first = session.Snapshot(1);
        switch (await _mocks.SaveProgressAsync(owner, first))
        {
            case SaveResult.Saved:
                Revision = first.Revision;
                _detached = false;
                return true;
            case SaveResult.OtherWriter:
                // Another tab started a mock between the check above and this write, and its mock wins. Only this
                // session's questions go: the delete skips a progress document of another session.
                await _mocks.ClearAsync(owner, sessionId);
                return false;
            default:
                // Not written (quota): later saves retry over the empty key.
                _detached = false;
                return true;
        }
    }

    // Decides what happens to a stored mock before a new one starts. Runs inside the write gate, so it calls the stores
    // directly. True when the stored mock is gone and the new one may start.
    private async Task<bool> ReleaseStoredAsync(MockProgress stored, IReadOnlyList<QuestionDto>? questions, long? telegramUserId)
    {
        string owner = stored.Owner;
        if (stored.Completion is not { } completion)
        {
            bool resumable = questions is not null && stored.Attempts is not null && questions.Count == stored.Attempts.Count;
            if (resumable || _mocks.HasNewerVersion)
            {
                // An unfinished mock is resumed, never replaced: it may be another tab's live one.
                return false;
            }

            // Its questions are gone: nothing can be graded or resumed, so nothing is lost by replacing it.
            return await _mocks.ClearAsync(owner, stored.SessionId);
        }

        long revision = stored.Revision;
        if (!completion.AnswersQueued)
        {
            if (!await _outbox.AddAllAsync(OutboxKey(telegramUserId), completion.Answers))
            {
                return false;
            }

            completion = completion with { AnswersQueued = true };
            (bool saved, revision) = await SaveFlagsAsync(stored, completion, revision);
            if (!saved)
            {
                return false;
            }
        }

        if (!completion.SummaryQueued)
        {
            if (telegramUserId is { } user && !(await _summaries.AddPendingAsync(user, stored.SessionId, completion.Summary)).Persisted)
            {
                return false;
            }

            completion = completion with { SummaryQueued = true };
            (bool saved, revision) = await SaveFlagsAsync(stored, completion, revision);
            if (!saved)
            {
                return false;
            }
        }

        // Both hand-overs are durable now; the flags only spare a repeat, so a flag save that failed does not matter.
        return await _mocks.ClearAsync(owner, stored.SessionId);
    }

    // Records a hand-over flag on the stored document. Only a write that happened moves the revision on; a failed one
    // (quota) leaves the stored document, and so the expected revision, as it was. False only for another writer.
    private async Task<(bool Proceed, long Revision)> SaveFlagsAsync(MockProgress stored, MockCompletionState completion, long revision)
    {
        SaveResult result = await _mocks.SaveProgressAsync(stored.Owner, stored with { Completion = completion, Revision = revision + 1 });
        return result switch
        {
            SaveResult.Saved => (true, revision + 1),
            SaveResult.OtherWriter => (false, revision),
            _ => (true, revision),
        };
    }

    private static string OutboxKey(long? telegramUserId) =>
        telegramUserId is { } user ? StorageKeys.UserOutbox(user) : StorageKeys.AnonymousOutbox;

    private async Task<bool> SaveCoreAsync(MockSession session)
    {
        // A save for another session than the one this instance owns is a leftover (a debounced save of an earlier mock):
        // writing it would find the new mock's revision and wrongly turn this tab read-only.
        if (IsReadOnly || _detached || (_activeSessionId is not null && !string.Equals(session.Context.SessionId, _activeSessionId, StringComparison.Ordinal)))
        {
            return false;
        }

        MockProgress next = session.Snapshot(Revision + 1);
        SaveResult result = await _mocks.SaveProgressAsync(session.Context.Owner, next);
        switch (result)
        {
            case SaveResult.Saved:
                Revision = next.Revision;
                return true;
            case SaveResult.OtherWriter:
                IsReadOnly = true;
                CancelDebounce();
                Changed?.Invoke();
                return false;
            default:
                return false;
        }
    }

    private async Task<bool> ClearCoreAsync(MockSession session)
    {
        if (!await _mocks.ClearAsync(session.Context.Owner, session.Context.SessionId))
        {
            return false;
        }

        Revision = 0;
        return true;
    }

    // One storage step at a time, in call order: a save must never overtake an earlier one with an older snapshot.
    private async Task<T> SerializedAsync<T>(Func<Task<T>> step)
    {
        await _writes.WaitAsync();
        try
        {
            return await step();
        }
        finally
        {
            _writes.Release();
        }
    }

    private Task<T> Bounded<T>(Task<T> work, T onTimeout) => _slow.WaitAsync(work, onTimeout);

    private void CancelDebounce()
    {
        lock (_timerGate)
        {
            _pending = null;
            _debounce?.Dispose();
            _debounce = null;
        }
    }
}
