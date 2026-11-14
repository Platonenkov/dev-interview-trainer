using Trainer.Application.Answers;

namespace Trainer.Application.Tests;

public sealed class AnswerTimePolicyTests
{
    private const long Day = 24L * 60 * 60 * 1000;
    private const long Minute = 60L * 1000;

    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-365 * Day - 1, TimeVerdict.TooOld)]
    [InlineData(-365 * Day, TimeVerdict.Accept)]
    [InlineData(-1L, TimeVerdict.Accept)]
    [InlineData(0L, TimeVerdict.Accept)]
    [InlineData(1L, TimeVerdict.Clamp)]
    [InlineData(5 * Minute, TimeVerdict.Clamp)]
    [InlineData(5 * Minute + 1, TimeVerdict.InFuture)]
    public void Judge_Boundaries(long offsetMilliseconds, TimeVerdict expected)
    {
        DateTimeOffset answeredAt = Now.AddMilliseconds(offsetMilliseconds);

        Assert.Equal(expected, AnswerTimePolicy.Judge(answeredAt, Now));
    }

    [Fact]
    public void Judge_ComparesInstantsNotOffsets()
    {
        // 13:03 at +03:00 is 10:03 UTC: three minutes ahead of the server, so it is clamped, not rejected.
        DateTimeOffset answeredAt = new(2026, 11, 12, 13, 3, 0, TimeSpan.FromHours(3));

        Assert.Equal(TimeVerdict.Clamp, AnswerTimePolicy.Judge(answeredAt, Now));
    }

    [Fact]
    public void Judge_ExtremeDates_DoNotThrow()
    {
        Assert.Equal(TimeVerdict.TooOld, AnswerTimePolicy.Judge(DateTimeOffset.MinValue, Now));
        Assert.Equal(TimeVerdict.InFuture, AnswerTimePolicy.Judge(DateTimeOffset.MaxValue, Now));
    }

    [Fact]
    public void Effective_ClampsToNow()
    {
        Assert.Equal(Now, AnswerTimePolicy.Effective(Now.AddMinutes(5), Now));
        Assert.Equal(Now, AnswerTimePolicy.Effective(Now.AddTicks(1), Now));
    }

    [Fact]
    public void Effective_AcceptedTime_IsUnchanged()
    {
        DateTimeOffset answeredAt = Now.AddDays(-30);

        Assert.Equal(answeredAt, AnswerTimePolicy.Effective(answeredAt, Now));
    }

    [Theory]
    [InlineData(-366 * Day)]
    [InlineData(6 * Minute)]
    public void Effective_RejectedTime_Throws(long offsetMilliseconds)
    {
        DateTimeOffset answeredAt = Now.AddMilliseconds(offsetMilliseconds);

        Assert.Throws<ArgumentOutOfRangeException>(() => AnswerTimePolicy.Effective(answeredAt, Now));
    }
}
