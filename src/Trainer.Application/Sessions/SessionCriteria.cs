using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;
using Trainer.Domain.Sessions;

namespace Trainer.Application.Sessions;

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
