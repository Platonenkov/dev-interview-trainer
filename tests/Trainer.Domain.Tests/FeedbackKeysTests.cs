using Trainer.Domain.Feedback;

namespace Trainer.Domain.Tests;

public sealed class FeedbackKeysTests
{
    [Theory]
    [InlineData("like", QuestionRating.Like)]
    [InlineData("dislike", QuestionRating.Dislike)]
    public void RatingKeys_Parse(string key, QuestionRating expected)
    {
        Assert.True(QuestionRatingKeys.TryParse(key, out QuestionRating rating));
        Assert.Equal(expected, rating);
        Assert.Equal(key, QuestionRatingKeys.ToKey(expected));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Like")]
    [InlineData("like ")]
    [InlineData("neutral")]
    public void RatingKeys_RejectUnknownAndWrongCase(string? key) => Assert.False(QuestionRatingKeys.TryParse(key, out _));

    [Fact]
    public void RatingKeys_ToKey_RejectsUndefinedValue() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => QuestionRatingKeys.ToKey((QuestionRating)0));

    [Fact]
    public void ReasonKeys_AreTheFixedList() =>
        Assert.Equal(["wrong-answer", "unclear-wording", "outdated", "broken-link", "other"], ReportReasonKeys.All.Select(ReportReasonKeys.ToKey));

    [Fact]
    public void ReasonKeys_RoundTrip()
    {
        foreach (ReportReason reason in ReportReasonKeys.All)
        {
            Assert.True(ReportReasonKeys.TryParse(ReportReasonKeys.ToKey(reason), out ReportReason back));
            Assert.Equal(reason, back);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Other")]
    [InlineData("wrong_answer")]
    public void ReasonKeys_RejectUnknownAndWrongCase(string? key) => Assert.False(ReportReasonKeys.TryParse(key, out _));

    [Fact]
    public void ReasonKeys_ToKey_RejectsUndefinedValue() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ReportReasonKeys.ToKey((ReportReason)99));

    [Fact]
    public void ReasonKeys_AllListIsReadOnly() => Assert.False(ReportReasonKeys.All is ReportReason[]);

    [Fact]
    public void ReportId_AcceptsUlidOnly()
    {
        Assert.True(ReportId.TryCreate("01JD0000000000000000000000", out ReportId id));
        Assert.Equal("01JD0000000000000000000000", id.Value);
        Assert.False(ReportId.TryCreate("not-a-ulid", out _));
        Assert.False(ReportId.TryCreate(null, out _));
    }
}
