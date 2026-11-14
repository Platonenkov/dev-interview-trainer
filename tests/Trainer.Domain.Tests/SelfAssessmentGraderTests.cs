using Trainer.Domain.Grading;
using Trainer.Domain.Questions;

namespace Trainer.Domain.Tests;

public sealed class SelfAssessmentGraderTests
{
    private static readonly QuestionId Id = new("01JC0000000000000000000000");

    [Fact]
    public async Task SelfAssessment_HintCapsKnew() =>
        Assert.Equal(Grade.Partial, await new SelfAssessmentGrader().GradeAsync(new(Id, QuestionType.Open, null, Grade.Knew, true), CancellationToken.None));

    [Theory]
    [InlineData(QuestionType.Open)]
    [InlineData(QuestionType.Situation)]
    public async Task SelfAssessment_ReturnsOwnGradeWithoutHint(QuestionType type) =>
        Assert.Equal(Grade.Knew, await new SelfAssessmentGrader().GradeAsync(new(Id, type, "text ignored in v1", Grade.Knew, false), CancellationToken.None));

    [Theory]
    [InlineData(QuestionType.Single)]
    [InlineData(QuestionType.Multi)]
    [InlineData(QuestionType.Output)]
    [InlineData(QuestionType.FindBug)]
    public async Task SelfAssessment_RejectsAutomaticTypes(QuestionType type) =>
        await Assert.ThrowsAsync<ArgumentException>(() => new SelfAssessmentGrader().GradeAsync(new(Id, type, null, Grade.Knew, false), CancellationToken.None).AsTask());
}
