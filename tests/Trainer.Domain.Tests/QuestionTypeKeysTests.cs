using Trainer.Domain.Questions;

namespace Trainer.Domain.Tests;

public sealed class QuestionTypeKeysTests
{
    [Fact]
    public void FindBug_RoundTrips()
    {
        Assert.Equal("find-bug", QuestionTypeKeys.ToKey(QuestionType.FindBug));
        Assert.True(QuestionTypeKeys.TryParse("find-bug", out QuestionType t));
        Assert.Equal(QuestionType.FindBug, t);
    }

    [Fact]
    public void AllTypes_RoundTripWithLowercaseKeys()
    {
        Assert.Equal(6, QuestionTypeKeys.All.Count);
        foreach (QuestionType type in QuestionTypeKeys.All)
        {
            string key = QuestionTypeKeys.ToKey(type);
            Assert.Equal(key.ToLowerInvariant(), key);
            Assert.True(QuestionTypeKeys.TryParse(key.ToUpperInvariant(), out QuestionType parsed));
            Assert.Equal(type, parsed);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("findbug")]
    public void TryParse_RejectsUnknownKeys(string? key) => Assert.False(QuestionTypeKeys.TryParse(key, out _));

    [Fact]
    public void Grading_RejectsLlmInV1() => Assert.False(GradingModeKeys.TryParse("llm", out _));

    [Theory]
    [InlineData("self", GradingMode.Self)]
    [InlineData("AUTO", GradingMode.Auto)]
    public void Grading_AcceptsSelfAndAuto(string key, GradingMode expected)
    {
        Assert.True(GradingModeKeys.TryParse(key, out GradingMode mode));
        Assert.Equal(expected, mode);
        Assert.Equal(key.ToLowerInvariant(), GradingModeKeys.ToKey(mode));
    }
}
