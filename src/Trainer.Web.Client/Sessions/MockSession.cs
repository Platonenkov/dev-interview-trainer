using Trainer.Contracts;
using Trainer.Contracts.Storage;
using Trainer.Domain.Grading;
using Trainer.Domain.Ids;
using Trainer.Web.Client.Api;

namespace Trainer.Web.Client.Sessions;

/// <summary>Where the Mock island is (spec 5.3). <see cref="MockSession"/> itself covers the last three.</summary>
public enum MockPhase
{
    Setup,

    /// <summary>The questions arrived; a small pool is warned about before anything is shown.</summary>
    Prepared,

    Answering,
    Assessment,
    Summary,
}

/// <summary>What a mock was started with; it never changes afterwards.</summary>
/// <param name="Owner">The Telegram user id as text, or <c>anon</c>.</param>
/// <param name="Limit">The time allowed when <paramref name="Timed"/>: <see cref="PerQuestion"/> per question.</param>
/// <param name="StartedAt">Server clock (ADR 0005).</param>
public sealed record MockContext(
    string SessionId,
    string Owner,
    string Stack,
    string Culture,
    string Level,
    bool Timed,
    TimeSpan? Limit,
    DateTimeOffset StartedAt)
{
    /// <summary>The time per question when the timer is on: a typical screening pace.</summary>
    public static readonly TimeSpan PerQuestion = TimeSpan.FromSeconds(90);
}

/// <summary>The setup form of a mock (spec 5.3): a level (required), optional topics, the count and the timer.</summary>
public sealed record MockSettings(string Level, IReadOnlyList<string> Topics, int Count, bool Timed)
{
    public const int DefaultCount = 20;
    public const int MinCount = 5;
    public const int MaxCount = 50;

    private const string DefaultLevel = "middle";

    /// <summary>The time the timer allows for <see cref="Count"/> questions, shown before the start.</summary>
    public TimeSpan TimeLimit => MockContext.PerQuestion * Count;

    /// <summary>
    /// The form's first values: the first level and the topics a catalog link carried, else Middle and all topics; the
    /// default count; the timer off (owner ruling).
    /// </summary>
    public static MockSettings From(IslandBootstrapDto bootstrap)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        string level = bootstrap.InitialFilters.Levels.FirstOrDefault(l => bootstrap.Levels.Contains(l, StringComparer.Ordinal))
            ?? (bootstrap.Levels.Contains(DefaultLevel, StringComparer.Ordinal) ? DefaultLevel : bootstrap.Levels[0]);
        return new MockSettings(level, [.. bootstrap.InitialFilters.Topics], DefaultCount, false);
    }

    public MockSessionRequest ToRequest(string culture, string stack) =>
        new(culture, stack, Level, Topics, Math.Clamp(Count, MinCount, MaxCount));

    /// <summary>
    /// Whether the small-pool warning offers "Use N" for the server's <c>SuggestedCount</c>: only a valid mock count below
    /// <see cref="Count"/>. A pool of 1-4 questions suggests its own size, which the server would clamp back to
    /// <see cref="MinCount"/> and which changes nothing, since the session already takes the whole pool.
    /// </summary>
    public bool OffersSuggestion(int suggestedCount) => suggestedCount >= MinCount && suggestedCount < Count;
}

/// <summary>
/// A mock interview from its first question to its completion (spec 5.3): no feedback while answering, then
/// <see cref="Finish"/> freezes every attempt and grades the automatic ones that have a response, the self-assessed ones
/// are graded or skipped in the assessment phase, and <see cref="Complete"/> yields the answers and the summary once.
/// Every attempt has its answer id from the start, so however often the mock is restored and completed, it yields the
/// same answers. Eligibility: an automatic attempt with a response is an answer at the time of its last input; a
/// self-assessed one only from an explicit self-grade, at the time of that grade; everything else is unanswered in the
/// summary and feeds no answer to the scheduler.
/// </summary>
public sealed class MockSession
{
    private const string AnsweringKey = "answering";
    private const string AssessmentKey = "assessment";
    private const string FinishedKey = "finished";

    private static readonly AnswerFactory AnswerFactory = new();
    private static readonly MockSummaryBuilder SummaryBuilder = new();

    private readonly List<QuestionAttempt> _attempts;
    private readonly ServerClock _clock;
    private readonly Lock _gate = new();

    public MockSession(IReadOnlyList<QuestionDto> questions, MockContext context, UlidGenerator ids, Random random, ServerClock clock)
    {
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(random);
        ArgumentNullException.ThrowIfNull(clock);
        if (questions.Count == 0)
        {
            throw new ArgumentException("A mock needs at least one question.", nameof(questions));
        }

        Context = context;
        _clock = clock;
        _attempts = [.. questions.Select(q => new QuestionAttempt(q, ids.Next(), random, clock))];
        Deadline = context.Timed && context.Limit is { } limit ? context.StartedAt + limit : null;
        Phase = MockPhase.Answering;
    }

    private MockSession(MockContext context, List<QuestionAttempt> attempts, ServerClock clock)
    {
        Context = context;
        _attempts = attempts;
        _clock = clock;
    }

    public MockContext Context { get; }

    public MockPhase Phase { get; private set; }

    public IReadOnlyList<QuestionAttempt> Attempts => _attempts;

    /// <summary>The zero-based position of the question on screen while answering.</summary>
    public int Index { get; private set; }

    public QuestionAttempt Current => _attempts[Index];

    /// <summary>When the time runs out (server clock); null without a timer.</summary>
    public DateTimeOffset? Deadline { get; private set; }

    /// <summary>When answering ended (server clock); null while answering.</summary>
    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>The stored completion once <see cref="Complete"/> ran; its flags record the hand-over steps done.</summary>
    public MockCompletionState? Completion { get; private set; }

    /// <summary>The self-assessed attempts, in order; all of them are listed in the assessment phase.</summary>
    public IReadOnlyList<QuestionAttempt> SelfAssessed => [.. _attempts.Where(a => a.Kind == QuestionKind.SelfAssessed)];

    /// <summary>Every self-assessed attempt not yet graded or skipped; empty while answering.</summary>
    public IReadOnlyList<QuestionAttempt> PendingAssessment =>
        Phase == MockPhase.Assessment ? [.. SelfAssessed.Where(a => !a.IsDone)] : [];

    /// <summary>The assessment phase has nothing left to grade, so <see cref="Complete"/> may run.</summary>
    public bool CanComplete => Phase == MockPhase.Summary
        || (Phase == MockPhase.Assessment && _attempts.All(a => a.Kind != QuestionKind.SelfAssessed || (a.IsDone && !a.IsAssessing)));

    /// <summary>
    /// The marker of the jump strip: a pick, a line, typed text, or notes on a self-assessed question. It says nothing about
    /// correctness.
    /// </summary>
    public static bool IsAnswered(QuestionAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        return attempt.Kind == QuestionKind.SelfAssessed ? attempt.Notes.Trim().Length > 0 : attempt.HasResponse;
    }

    /// <summary>Moves to another question while answering; out-of-range indexes and other phases are ignored.</summary>
    public void GoTo(int index)
    {
        if (Phase == MockPhase.Answering && index >= 0 && index < _attempts.Count)
        {
            Index = index;
        }
    }

    /// <summary>
    /// Time left at <paramref name="now"/> (server clock), never negative. It is computed from the deadline, never from
    /// timer ticks, so a throttled background tab shows the right value as soon as it runs again.
    /// <see cref="Timeout.InfiniteTimeSpan"/> without a timer.
    /// </summary>
    public TimeSpan Remaining(DateTimeOffset now)
    {
        if (Deadline is not { } deadline)
        {
            return Timeout.InfiniteTimeSpan;
        }

        TimeSpan left = deadline - now;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    public bool IsExpired(DateTimeOffset now) => Deadline is { } deadline && now >= deadline;

    /// <summary>
    /// Ends answering: freezes every attempt, grades the automatic ones that have a response (dated at their last
    /// input), shows the reference answer of every self-assessed one and moves to <see cref="MockPhase.Assessment"/>.
    /// The finish button and the timer may both call it at the same moment: only the first call does anything.
    /// </summary>
    /// <returns>True for the call that finished the mock; false when it was already finished.</returns>
    public bool Finish()
    {
        lock (_gate)
        {
            if (Phase != MockPhase.Answering)
            {
                return false;
            }

            // A mock resumed long after its deadline ended at the deadline, not when the page came back.
            DateTimeOffset now = _clock.Now();
            FinishedAt = Deadline is { } deadline && now > deadline ? deadline : now;
            foreach (QuestionAttempt attempt in _attempts)
            {
                attempt.Freeze();
                if (attempt.Kind == QuestionKind.SelfAssessed)
                {
                    attempt.RevealReference();
                }
                else if (attempt.HasResponse && !attempt.IsDone)
                {
                    attempt.SubmitAutomaticAtLastInput();
                }
            }

            Phase = MockPhase.Assessment;
            return true;
        }
    }

    /// <summary>Records the user's grade for the self-assessed attempt at <paramref name="index"/> (an index into <see cref="Attempts"/>).</summary>
    /// <exception cref="InvalidOperationException">Not in the assessment phase, or the attempt cannot take a grade now.</exception>
    public async ValueTask AssessAsync(int index, Grade choice, IAnswerGrader grader, CancellationToken ct)
    {
        QuestionAttempt attempt = Assessable(index);
        await attempt.SubmitSelfAssessmentAsync(choice, grader, ct);
    }

    /// <summary>Leaves the self-assessed attempt at <paramref name="index"/> unanswered.</summary>
    /// <exception cref="InvalidOperationException">Not in the assessment phase, or the attempt is already graded.</exception>
    public void SkipAssessment(int index) => Assessable(index).Skip();

    /// <summary>
    /// Builds the answers (with the attempts' ids and times) and the summary, stores them as <see cref="Completion"/> and
    /// moves to <see cref="MockPhase.Summary"/>. Idempotent: a later call returns the stored instance.
    /// </summary>
    /// <exception cref="InvalidOperationException">A self-assessed attempt is still waiting for its grade or skip.</exception>
    public MockCompletionState Complete()
    {
        lock (_gate)
        {
            if (Completion is { } stored)
            {
                return stored;
            }

            if (!CanComplete)
            {
                throw new InvalidOperationException("The mock can be completed only after the assessment.");
            }

            IReadOnlyList<AnswerDto> answers =
                [.. _attempts.Where(a => a.Grade is not null).Select(a => AnswerFactory.Create(a, AnswerMode.Mock, Context.SessionId))];
            DateTimeOffset finishedAt = FinishedAt ?? _clock.Now();
            Completion = new MockCompletionState(finishedAt, answers, SummaryBuilder.Build(this, finishedAt), false, false);
            Phase = MockPhase.Summary;
            return Completion;
        }
    }

    /// <summary>The answers are in the outbox (hand-over step 2).</summary>
    public void MarkAnswersQueued() => Completion = RequireCompletion() with { AnswersQueued = true };

    /// <summary>The summary is in the pending queue, or there is nothing to queue (hand-over step 3).</summary>
    public void MarkSummaryQueued() => Completion = RequireCompletion() with { SummaryQueued = true };

    /// <summary>The whole mock as the small progress document; the questions are stored once, elsewhere.</summary>
    public MockProgress Snapshot(long revision)
    {
        lock (_gate)
        {
            return new MockProgress(
                MockProgress.CurrentVersion,
                Context.SessionId,
                Context.Owner,
                Context.Stack,
                Context.Culture,
                Context.Level,
                _attempts.Count,
                Context.Timed,
                Context.StartedAt,
                Deadline,
                PhaseKey(Phase),
                Index,
                revision,
                [.. _attempts.Select(a => a.Snapshot())],
                Completion,
                FinishedAt);
        }
    }

    /// <summary>Rebuilds a mock exactly as saved; <paramref name="questions"/> are the ones stored with it, in order.</summary>
    /// <exception cref="ArgumentException">The questions do not match the saved attempts, or the phase is unknown.</exception>
    public static MockSession Restore(MockProgress progress, IReadOnlyList<QuestionDto> questions, ServerClock clock)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(clock);
        if (progress.Attempts is null || questions.Count == 0 || questions.Count != progress.Attempts.Count)
        {
            throw new ArgumentException("The stored questions do not match the stored mock.", nameof(questions));
        }

        MockPhase phase = progress.Phase switch
        {
            AnsweringKey => MockPhase.Answering,
            AssessmentKey => MockPhase.Assessment,
            FinishedKey when progress.Completion is not null => MockPhase.Summary,
            _ => throw new ArgumentException($"Unknown mock phase '{progress.Phase}'.", nameof(progress)),
        };

        TimeSpan? limit = progress.Deadline is { } deadline ? deadline - progress.StartedAt : null;
        MockContext context = new(
            progress.SessionId, progress.Owner, progress.Stack, progress.Culture, progress.Level, progress.Timed, limit, progress.StartedAt);
        List<QuestionAttempt> attempts = [.. questions.Select((q, i) => QuestionAttempt.Restore(q, progress.Attempts[i], clock))];
        MockSession session = new(context, attempts, clock)
        {
            Phase = phase,
            Deadline = progress.Deadline,
            FinishedAt = progress.FinishedAt ?? progress.Completion?.FinishedAt,
            Completion = progress.Completion,
            Index = Math.Clamp(progress.Index, 0, attempts.Count - 1),
        };

        if (phase != MockPhase.Answering)
        {
            // A document saved mid-finish must not leave an attempt open after the end.
            foreach (QuestionAttempt attempt in attempts)
            {
                attempt.Freeze();
                if (attempt.Kind == QuestionKind.SelfAssessed)
                {
                    attempt.RevealReference();
                }
            }
        }

        return session;
    }

    private static string PhaseKey(MockPhase phase) => phase switch
    {
        MockPhase.Answering => AnsweringKey,
        MockPhase.Assessment => AssessmentKey,
        _ => FinishedKey,
    };

    private QuestionAttempt Assessable(int index)
    {
        if (Phase != MockPhase.Assessment || index < 0 || index >= _attempts.Count)
        {
            throw new InvalidOperationException("Only a self-assessed question can be assessed, and only in the assessment phase.");
        }

        return _attempts[index];
    }

    private MockCompletionState RequireCompletion() =>
        Completion ?? throw new InvalidOperationException("The mock is not complete yet.");
}
