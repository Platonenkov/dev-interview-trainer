using Trainer.Domain.Questions;
using Trainer.Domain.Sessions;

namespace Trainer.Domain.Tests;

public sealed class ReviewOrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private static QuestionId Id(string value) => new(value);

    [Fact]
    public void ReviewOrder_MostOverdueFirstThenId_DueAndLiveOnly()
    {
        (QuestionId, DateTimeOffset)[] states =
        [
            (Id("B"), Now.AddDays(-1)),
            (Id("A"), Now.AddDays(-1)),
            (Id("C"), Now.AddDays(-5)),
            (Id("D"), Now),
            (Id("E"), Now.AddSeconds(1)),
            (Id("F"), Now.AddDays(-9)),
        ];
        HashSet<QuestionId> live = [Id("A"), Id("B"), Id("C"), Id("D"), Id("E")];

        (IReadOnlyList<QuestionId> ids, int poolSize) = ReviewOrder.Pick(states, live, Now, 50);

        Assert.Equal([Id("C"), Id("A"), Id("B"), Id("D")], ids);
        Assert.Equal(4, poolSize);
    }

    [Fact]
    public void ReviewOrder_CountLimitsIdsButNotPool()
    {
        (QuestionId, DateTimeOffset)[] states = [(Id("A"), Now.AddDays(-3)), (Id("B"), Now.AddDays(-2)), (Id("C"), Now.AddDays(-1))];
        HashSet<QuestionId> live = [Id("A"), Id("B"), Id("C")];

        (IReadOnlyList<QuestionId> ids, int poolSize) = ReviewOrder.Pick(states, live, Now, 2);

        Assert.Equal([Id("A"), Id("B")], ids);
        Assert.Equal(3, poolSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void ReviewOrder_CountOutOfRange_Throws(int count) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ReviewOrder.Pick([], new HashSet<QuestionId>(), Now, count));
}
