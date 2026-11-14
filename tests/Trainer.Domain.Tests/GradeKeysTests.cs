using Trainer.Domain.Grading;

namespace Trainer.Domain.Tests;

public sealed class GradeKeysTests
{
    [Fact]
    public void GradeKeys_RoundTrip()
    {
        foreach (Grade g in GradeKeys.All)
        {
            Assert.True(GradeKeys.TryParse(GradeKeys.ToKey(g), out Grade back));
            Assert.Equal(g, back);
        }
    }

    [Fact]
    public void GradeKeys_RejectUnknown() => Assert.False(GradeKeys.TryParse("KNEW", out _));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("knew ")]
    public void GradeKeys_RejectNullEmptyAndPadded(string? key) => Assert.False(GradeKeys.TryParse(key, out _));

    [Fact]
    public void GradeKeys_UsesWireKeys() =>
        Assert.Equal(["didnt-know", "partial", "knew"], GradeKeys.All.Select(GradeKeys.ToKey));

    [Fact]
    public void GradeKeys_ToKey_RejectsUndefinedValue() => Assert.Throws<ArgumentOutOfRangeException>(() => GradeKeys.ToKey((Grade)9));

    [Fact]
    public void AnswerModeKeys_RoundTrip()
    {
        foreach (AnswerMode m in AnswerModeKeys.All)
        {
            Assert.True(AnswerModeKeys.TryParse(AnswerModeKeys.ToKey(m), out AnswerMode back));
            Assert.Equal(m, back);
        }
    }

    [Fact]
    public void AnswerModeKeys_UsesWireKeys() =>
        Assert.Equal(["practice", "mock", "review"], AnswerModeKeys.All.Select(AnswerModeKeys.ToKey));

    [Fact]
    public void AnswerModeKeys_RejectUnknown() => Assert.False(AnswerModeKeys.TryParse("Practice", out _));

    [Fact]
    public void Keys_AllListsAreNotMutableThroughCasts()
    {
        Assert.False(GradeKeys.All is Grade[]);
        Assert.False(AnswerModeKeys.All is AnswerMode[]);
    }
}
