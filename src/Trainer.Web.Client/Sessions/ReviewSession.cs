using Trainer.Contracts;
using Trainer.Domain.Grading;
using Trainer.Domain.Ids;
using Trainer.Web.Client.Api;

namespace Trainer.Web.Client.Sessions;

/// <summary>
/// A Review run (spec 5.3): Practice's one-at-a-time session over the user's due questions, with a recall step. To train
/// recall rather than recognition, a choice question first shows its prompt without options and asks whether the user
/// can answer; <see cref="ShowOptions"/> then reveals the options in the attempt's shuffled order. Answers carry
/// <c>mode = review</c>.
/// </summary>
public sealed class ReviewSession
{
    private readonly PracticeSession _session;
    private bool _optionsShown;

    public ReviewSession(IReadOnlyList<QuestionDto> questions, UlidGenerator ids, Random random, ServerClock clock)
    {
        _session = new PracticeSession(questions, ids, random, clock);
    }

    public bool IsEmpty => _session.IsEmpty;

    /// <summary>The zero-based position of <see cref="Current"/>; equals <see cref="Count"/> once finished.</summary>
    public int Index => _session.Index;

    public int Count => _session.Count;

    /// <summary>The attempt on screen; null when the session is empty or finished.</summary>
    public QuestionAttempt? Current => _session.Current;

    public bool IsFinished => _session.IsFinished;

    /// <summary>Every graded question so far, in order.</summary>
    public IReadOnlyList<(QuestionDto Question, Grade Grade)> Results => _session.Results;

    /// <summary>A choice question whose options are still hidden: the island asks "Can you answer?" instead.</summary>
    public bool AwaitingRecall => Current is { } attempt && attempt.Kind.IsChoice() && !_optionsShown;

    /// <summary>Whether the current attempt may be submitted now; never while its options are hidden.</summary>
    public bool CanSubmit => !AwaitingRecall && Current?.CanSubmit == true;

    /// <summary>Reveals the options of the current choice question, in the order the attempt shuffled them.</summary>
    public void ShowOptions()
    {
        if (Current is not null)
        {
            _optionsShown = true;
        }
    }

    /// <summary>Picks an option of the current attempt; ignored while the options are hidden.</summary>
    public void Pick(string optionId)
    {
        if (!AwaitingRecall)
        {
            Current?.Toggle(optionId);
        }
    }

    /// <summary>Moves to the next question, which starts hidden again if it is a choice question.</summary>
    public void Next()
    {
        if (IsFinished)
        {
            return;
        }

        _session.Next();
        _optionsShown = false;
    }
}
