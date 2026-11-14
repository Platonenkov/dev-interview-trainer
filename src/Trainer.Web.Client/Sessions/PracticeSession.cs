using Trainer.Contracts;
using Trainer.Domain.Grading;
using Trainer.Domain.Ids;
using Trainer.Web.Client.Api;

namespace Trainer.Web.Client.Sessions;

/// <summary>
/// A Practice (or Review) run over a fixed list of questions, one at a time. Each question gets its attempt, and with
/// it its answer id, when it is first shown.
/// </summary>
public sealed class PracticeSession
{
    private readonly IReadOnlyList<QuestionDto> _questions;
    private readonly UlidGenerator _ids;
    private readonly Random _random;
    private readonly ServerClock _clock;
    private readonly List<QuestionAttempt> _shown = [];

    public PracticeSession(IReadOnlyList<QuestionDto> questions, UlidGenerator ids, Random random, ServerClock clock)
    {
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(random);
        ArgumentNullException.ThrowIfNull(clock);

        _questions = [.. questions];
        _ids = ids;
        _random = random;
        _clock = clock;
        Show();
    }

    public bool IsEmpty => _questions.Count == 0;

    /// <summary>The zero-based position of <see cref="Current"/>; equals <see cref="Count"/> once finished.</summary>
    public int Index { get; private set; }

    public int Count => _questions.Count;

    /// <summary>The attempt on screen; null when the session is empty or finished.</summary>
    public QuestionAttempt? Current => IsFinished ? null : _shown[Index];

    public bool IsFinished => Index >= _questions.Count;

    /// <summary>Every graded question so far, in order; skipped and unanswered ones have no result.</summary>
    public IReadOnlyList<(QuestionDto Question, Grade Grade)> Results =>
        [.. _shown.Where(a => a.Grade is not null).Select(a => (a.Question, a.Grade!.Value))];

    /// <summary>Distinct ids of the questions shown so far, so "New session" can exclude them.</summary>
    public IReadOnlyList<string> SeenIds => [.. _shown.Select(a => a.Question.Id).Distinct(StringComparer.Ordinal)];

    /// <summary>Moves to the next question, answered or not; does nothing once finished.</summary>
    public void Next()
    {
        if (IsFinished)
        {
            return;
        }

        Index++;
        Show();
    }

    private void Show()
    {
        if (!IsFinished)
        {
            _shown.Add(new QuestionAttempt(_questions[Index], _ids.Next(), _random, _clock));
        }
    }
}
