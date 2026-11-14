using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;

namespace Trainer.Application.Sessions;

/// <summary>Which of the stack's questions Practice offers, relative to the user's review states.</summary>
public enum PracticeStatus
{
    All,

    /// <summary>Never answered: no review state for a signed-in user; for an anonymous one the client lists answered questions in <c>Exclude</c>.</summary>
    New,

    /// <summary>Review state due now. Needs a signed-in user; an anonymous query matches nothing.</summary>
    Due,
}

/// <summary>
/// Practice query. An empty <paramref name="Levels"/>, <paramref name="Topics"/> or <paramref name="Types"/> list does not
/// restrict. <paramref name="First"/> opens the session when it is live in the stack and culture, whatever the filters say, and
/// counts toward <paramref name="Count"/>. <paramref name="Exclude"/> hides questions in every status: an anonymous
/// <c>New</c> query sends the questions answered in the browser (the client is the only record of them), and "New session"
/// sends the questions just seen.
/// </summary>
public sealed record PracticeCriteria(
    CultureKey Culture,
    StackKey Stack,
    IReadOnlyList<Level> Levels,
    IReadOnlyList<TopicKey> Topics,
    IReadOnlyList<QuestionType> Types,
    PracticeStatus Status,
    int Count,
    QuestionId? First,
    IReadOnlyList<QuestionId> Exclude);

/// <summary>Mock query: <paramref name="Count"/> questions of <paramref name="Level"/> and the level below, in the given topics (none = all).</summary>
public sealed record MockCriteria(CultureKey Culture, StackKey Stack, Level Level, IReadOnlyList<TopicKey> Topics, int Count);

public sealed record ReviewCriteria(CultureKey Culture, StackKey Stack, int Count);

/// <summary>Bounds of the session queries; the API validates requests against them and the query services enforce them.</summary>
public static class SessionLimits
{
    public const int DefaultCount = 20;
    public const int MaxCount = 50;
    public const int MinPracticeCount = 1;
    public const int MinMockCount = 5;
    public const int MinReviewCount = 1;
    public const int MaxExcludedQuestions = 1000;
}
