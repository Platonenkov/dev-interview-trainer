using Trainer.Domain.Levels;
using Trainer.Domain.Questions;

namespace Trainer.Application.Sessions;

/// <summary>
/// Chooses the questions of a mock interview: about 30% from the level below (none for Junior), the rest at the level, a
/// bucket that runs short filled from the other, and the result shuffled. Not thread-safe unless the injected
/// <see cref="Random"/> is (use <see cref="Random.Shared"/> for a singleton).
/// </summary>
public sealed class MockComposer(Random random)
{
    public const double LowerLevelShare = 0.3;

    private const int MinSuggested = 5;
    private const int MaxSuggested = SessionLimits.MaxCount;
    private const int RepeatPoolFactor = 3;

    /// <param name="atLevel">Candidates at <paramref name="level"/>.</param>
    /// <param name="below">Candidates one level lower; ignored for Junior.</param>
    /// <returns>At most <paramref name="count"/> distinct ids; every candidate when there are not enough of them.</returns>
    public IReadOnlyList<QuestionId> Compose(
        Level level, IReadOnlyList<QuestionId> atLevel, IReadOnlyList<QuestionId> below, int count)
    {
        ArgumentNullException.ThrowIfNull(atLevel);
        ArgumentNullException.ThrowIfNull(below);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        IReadOnlyList<QuestionId> lower = level == Level.Junior ? [] : below;
        int fromLower = Math.Min((int)Math.Round(count * LowerLevelShare, MidpointRounding.AwayFromZero), lower.Count);
        int fromLevel = Math.Min(count - fromLower, atLevel.Count);
        fromLower = Math.Min(count - fromLevel, lower.Count);

        List<QuestionId> picked = [.. Shuffled(atLevel).Take(fromLevel), .. Shuffled(lower).Take(fromLower)];
        Shuffle(picked);
        return picked;
    }

    /// <summary>The spec warns before the session starts when the pool is smaller than about three times the count.</summary>
    public static bool RepeatsLikely(int poolSize, int count) => poolSize < RepeatPoolFactor * count;

    /// <summary>
    /// The count that keeps repeats unlikely: a third of the pool within the mock range 5..50, the whole pool when it holds
    /// fewer than 5 questions (the session then uses all of it), 0 for an empty pool. Below 5 it is therefore no valid mock
    /// count: a client offers "Use N" only when this is at least 5 and smaller than the count it asked for.
    /// </summary>
    public static int SuggestedCount(int poolSize)
    {
        if (poolSize <= 0)
        {
            return 0;
        }

        return poolSize < MinSuggested ? poolSize : Math.Clamp(poolSize / RepeatPoolFactor, MinSuggested, MaxSuggested);
    }

    private List<QuestionId> Shuffled(IReadOnlyList<QuestionId> source)
    {
        List<QuestionId> copy = [.. source];
        Shuffle(copy);
        return copy;
    }

    private void Shuffle(List<QuestionId> items)
    {
        for (int i = items.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}
