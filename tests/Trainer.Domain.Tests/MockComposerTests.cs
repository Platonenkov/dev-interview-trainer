using Trainer.Domain.Levels;
using Trainer.Domain.Questions;
using Trainer.Domain.Sessions;

namespace Trainer.Domain.Tests;

public sealed class MockComposerTests
{
    private static MockComposer Composer(int seed = 7) => new(new Random(seed));

    private static QuestionId[] Pool(string prefix, int size) =>
        [.. Enumerable.Range(0, size).Select(i => new QuestionId($"{prefix}{i:D3}"))];

    private static int CountFrom(IEnumerable<QuestionId> picked, IEnumerable<QuestionId> pool)
    {
        HashSet<QuestionId> set = [.. pool];
        return picked.Count(set.Contains);
    }

    [Fact]
    public void Compose_Middle20_Takes6FromJunior()
    {
        QuestionId[] atLevel = Pool("M", 40);
        QuestionId[] below = Pool("J", 40);

        IReadOnlyList<QuestionId> picked = Composer().Compose(Level.Middle, atLevel, below, 20);

        Assert.Equal(20, picked.Count);
        Assert.Equal(20, picked.Distinct().Count());
        Assert.Equal(6, CountFrom(picked, below));
        Assert.Equal(14, CountFrom(picked, atLevel));
    }

    [Fact]
    public void Compose_Junior_TakesNoLowerLevel()
    {
        QuestionId[] atLevel = Pool("J", 40);
        QuestionId[] ignored = Pool("X", 40);

        IReadOnlyList<QuestionId> picked = Composer().Compose(Level.Junior, atLevel, ignored, 20);

        Assert.Equal(20, picked.Count);
        Assert.Equal(20, CountFrom(picked, atLevel));
    }

    [Fact]
    public void Compose_ShortLowerPool_FillsFromLevel()
    {
        QuestionId[] atLevel = Pool("M", 40);
        QuestionId[] below = Pool("J", 2);

        IReadOnlyList<QuestionId> picked = Composer().Compose(Level.Middle, atLevel, below, 20);

        Assert.Equal(20, picked.Count);
        Assert.Equal(2, CountFrom(picked, below));
        Assert.Equal(18, CountFrom(picked, atLevel));
    }

    [Fact]
    public void Compose_ShortLevelPool_FillsFromLowerLevel()
    {
        QuestionId[] atLevel = Pool("M", 3);
        QuestionId[] below = Pool("J", 40);

        IReadOnlyList<QuestionId> picked = Composer().Compose(Level.Middle, atLevel, below, 20);

        Assert.Equal(20, picked.Count);
        Assert.Equal(3, CountFrom(picked, atLevel));
        Assert.Equal(17, CountFrom(picked, below));
    }

    [Fact]
    public void Compose_PoolSmallerThanCount_ReturnsAll()
    {
        QuestionId[] atLevel = Pool("M", 4);
        QuestionId[] below = Pool("J", 3);

        IReadOnlyList<QuestionId> picked = Composer().Compose(Level.Middle, atLevel, below, 20);

        Assert.Equal(
            atLevel.Concat(below).Select(q => q.Value).Order(StringComparer.Ordinal),
            picked.Select(q => q.Value).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Compose_ShufflesTheResult()
    {
        QuestionId[] atLevel = Pool("M", 40);
        QuestionId[] below = Pool("J", 40);

        IReadOnlyList<QuestionId> first = Composer(1).Compose(Level.Middle, atLevel, below, 20);
        IReadOnlyList<QuestionId> second = Composer(2).Compose(Level.Middle, atLevel, below, 20);

        Assert.NotEqual(first, second);
        Assert.False(first.Take(6).All(q => q.Value.StartsWith('J')), "lower-level questions must be mixed in, not first");
    }

    [Theory]
    [InlineData(59, 20, true)]
    [InlineData(60, 20, false)]
    [InlineData(0, 5, true)]
    public void RepeatsLikely_UnderThreeTimesN(int poolSize, int count, bool expected) =>
        Assert.Equal(expected, MockComposer.RepeatsLikely(poolSize, count));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    [InlineData(5, 5)]
    [InlineData(14, 5)]
    [InlineData(30, 10)]
    [InlineData(400, 50)]
    [InlineData(151, 50)]
    [InlineData(10_000, 50)]
    public void SuggestedCount_StaysInMockRangeAndUsesWholeSmallPool(int poolSize, int expected) =>
        Assert.Equal(expected, MockComposer.SuggestedCount(poolSize));
}
