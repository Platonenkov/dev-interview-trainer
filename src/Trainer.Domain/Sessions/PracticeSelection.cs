using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;

namespace Trainer.Domain.Sessions;

/// <summary>
/// What a Practice session asks for. An empty <paramref name="Levels"/>, <paramref name="Topics"/> or
/// <paramref name="Types"/> list does not restrict. <paramref name="First"/> opens the session when it is live, whatever
/// the filters and <paramref name="Exclude"/> say, and counts toward <paramref name="Count"/>.
/// <paramref name="Exclude"/> hides questions in every status.
/// </summary>
public sealed record PracticeSelection(
    IReadOnlyList<Level> Levels,
    IReadOnlyList<TopicKey> Topics,
    IReadOnlyList<QuestionType> Types,
    PracticeStatus Status,
    int Count,
    QuestionId? First,
    IReadOnlyCollection<QuestionId> Exclude);

/// <summary>The chosen question ids in session order and the size of the pool they were drawn from.</summary>
public sealed record PracticePick(IReadOnlyList<QuestionId> Ids, int PoolSize);
