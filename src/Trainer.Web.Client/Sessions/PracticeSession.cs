using Trainer.Contracts;
using Trainer.Domain.Grading;
using Trainer.Domain.Ids;
using Trainer.Web.Client.Api;
using Trainer.Web.Client.Backend;

namespace Trainer.Web.Client.Sessions;

/// <summary>
/// A Practice (or Review) run over a fixed list of question ids, one at a time. Questions come from the plan's
/// <see cref="IQuestionSource"/> as the run reaches them, and the one after the question on screen is prefetched. Each
/// question gets its attempt, and with it its answer id, when it is first shown. A load that fails leaves
/// <see cref="Index"/> and <see cref="Results"/> as they were, so asking again repeats it.
/// </summary>
public sealed class PracticeSession
{
    private readonly SessionPlan _plan;
    private readonly List<string> _questionIds;
    private readonly UlidGenerator _ids;
    private readonly Random _random;
    private readonly ServerClock _clock;
    private readonly List<QuestionAttempt> _shown = [];
    private Task<SessionRemainder>? _remainder;
    private bool _loading;

    public PracticeSession(SessionPlan plan, UlidGenerator ids, Random random, ServerClock clock)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(random);
        ArgumentNullException.ThrowIfNull(clock);

        _plan = plan;
        _questionIds = [.. plan.QuestionIds];
        _remainder = plan.Remainder;
        _ids = ids;
        _random = random;
        _clock = clock;
    }

    public bool IsEmpty => _questionIds.Count == 0 && _remainder is null;

    /// <summary>The zero-based position of the question last shown; equals <see cref="Count"/> once finished.</summary>
    public int Index { get; private set; }

    /// <summary>The number of questions so far; grows once when <see cref="SelectionPending"/> ends.</summary>
    public int Count => _questionIds.Count;

    /// <summary>
    /// The plan holds only its opening question while the rest is still being selected; the session cannot move on until
    /// <see cref="CompleteSelectionAsync"/> has finished.
    /// </summary>
    public bool SelectionPending => _remainder is not null;

    /// <summary>The attempt on screen; null while a question is loading and when the session is empty or finished.</summary>
    public QuestionAttempt? Current => _loading || IsFinished || _shown.Count <= Index ? null : _shown[Index];

    public bool IsFinished => Index >= _questionIds.Count && _remainder is null;

    /// <summary>Every graded question so far, in order; skipped and unanswered ones have no result.</summary>
    public IReadOnlyList<(QuestionDto Question, Grade Grade)> Results =>
        [.. _shown.Where(a => a.Grade is not null).Select(a => (a.Question, a.Grade!.Value))];

    /// <summary>Distinct ids of the questions shown so far, so "New session" can exclude them.</summary>
    public IReadOnlyList<string> SeenIds => [.. _shown.Select(a => a.Question.Id).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Waits for the rest of a plan that arrived with only its opening question and appends it. A selection that failed
    /// leaves the session at the questions it already has. Does nothing for a complete plan.
    /// </summary>
    public async Task CompleteSelectionAsync(CancellationToken ct)
    {
        if (_remainder is not { } pending)
        {
            return;
        }

        SessionRemainder? remainder = null;
        try
        {
            remainder = await pending.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A selection that failed ends the session after the opening question rather than failing it.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        // Two callers (the island's background wait and Next) may both get here; the first one appends.
        if (!ReferenceEquals(_remainder, pending))
        {
            return;
        }

        _remainder = null;
        if (remainder is not null)
        {
            _questionIds.AddRange(remainder.QuestionIds);
            // The opening question is on screen already, so the one after it was not known to prefetch until now.
            if (_shown.Count > 0 && Index + 1 < _questionIds.Count)
            {
                _plan.Questions.Prefetch(_questionIds[Index + 1]);
            }
        }
    }

    /// <summary>
    /// Loads and shows the first question. Succeeds with no question when the session is empty; after a failure it can be
    /// called again.
    /// </summary>
    public Task<ApiResult<QuestionDto>> StartAsync(CancellationToken ct) => ShowAsync(0, ct);

    /// <summary>
    /// Moves to the next question, answered or not, and waits for it to load; <see cref="Current"/> is null meanwhile.
    /// Succeeds with no question once finished. After a failure the position stays and the call can be repeated.
    /// </summary>
    public Task<ApiResult<QuestionDto>> NextAsync(CancellationToken ct)
    {
        if (IsFinished)
        {
            return Task.FromResult(NothingToShow);
        }

        // Starting is also the retry of a first question that failed to load.
        return ShowAsync(_shown.Count == 0 ? 0 : Index + 1, ct);
    }

    private static ApiResult<QuestionDto> NothingToShow => new(null, 200, null);

    private async Task<ApiResult<QuestionDto>> ShowAsync(int position, CancellationToken ct)
    {
        if (position >= _questionIds.Count && _remainder is not null)
        {
            await CompleteSelectionAsync(ct);
        }

        if (position >= _questionIds.Count)
        {
            Index = _questionIds.Count;
            return NothingToShow;
        }

        _loading = true;
        ApiResult<QuestionDto> result;
        try
        {
            result = await _plan.Questions.GetAsync(_questionIds[position], ct);
        }
        finally
        {
            _loading = false;
        }

        if (result.Value is not { } question || !result.IsSuccess)
        {
            return result.IsSuccess ? new ApiResult<QuestionDto>(null, 502, null) : result;
        }

        Index = position;
        _shown.Add(new QuestionAttempt(question, _ids.Next(), _random, _clock));
        if (position + 1 < _questionIds.Count)
        {
            _plan.Questions.Prefetch(_questionIds[position + 1]);
        }

        return result;
    }
}
