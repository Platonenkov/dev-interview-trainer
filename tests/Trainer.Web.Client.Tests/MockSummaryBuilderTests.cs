using Microsoft.Extensions.Time.Testing;
using Trainer.Contracts;
using Trainer.Domain.Grading;
using Trainer.Domain.Ids;
using Trainer.Web.Client.Api;
using Trainer.Web.Client.Sessions;

namespace Trainer.Web.Client.Tests;

public sealed class MockSummaryBuilderTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SummaryBuilder_CountsPerTopicIncludingUnanswered()
    {
        QuestionDto knew = TestQuestions.Single(correct: "a");
        QuestionDto wrong = TestQuestions.Single(correct: "a");
        QuestionDto unanswered = TestQuestions.Multi() with { Topic = "async", TopicTitle = "Async" };
        QuestionDto partial = TestQuestions.Open() with { Topic = "async", TopicTitle = "Async" };
        QuestionDto skipped = TestQuestions.Open("situation");
        FakeTimeProvider device = new(Start);
        ServerClock clock = new(device, new FakeBrowserStore());
        MockContext context = new("01KC0000000000000000000009", "anon", "dotnet", "en", "senior", true, TimeSpan.FromSeconds(450), Start);
        MockSession session = new([knew, unanswered, wrong, partial, skipped], context, new UlidGenerator(device), new Random(1), clock);
        session.Attempts[0].Toggle("a");
        session.Attempts[2].Toggle("b");
        session.Finish();
        await session.AssessAsync(3, Grade.Partial, new SelfAssessmentGrader(), Ct);
        session.SkipAssessment(4);

        MockSummaryDto summary = new MockSummaryBuilder().Build(session, Start.AddMinutes(6));

        Assert.Equal("dotnet", summary.Stack);
        Assert.Equal("senior", summary.Level);
        Assert.Equal(5, summary.QuestionCount);
        Assert.True(summary.Timed);
        Assert.Equal(Start, summary.StartedAt);
        Assert.Equal(Start.AddMinutes(6), summary.FinishedAt);
        Assert.Equal(
            [new MockTopicResultDto("closures", 1, 0, 1, 1), new MockTopicResultDto("async", 0, 1, 0, 1)],
            summary.Topics);
        Assert.Equal(summary.QuestionCount, summary.Topics.Sum(t => t.Knew + t.Partial + t.DidntKnow + t.Unanswered));
    }
}
