using Microsoft.Extensions.Time.Testing;
using Trainer.Contracts;
using Trainer.Domain.Grading;
using Trainer.Domain.Ids;
using Trainer.Web.Client.Api;
using Trainer.Web.Client.Sessions;

namespace Trainer.Web.Client.Tests;

public sealed class ReviewSessionTests
{
    private static readonly DateTimeOffset DeviceStart = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private static async Task<ReviewSession> CreateAsync(int seed, params QuestionDto[] questions)
    {
        FakeTimeProvider device = new(DeviceStart);
        ServerClock clock = new(device, new FakeBrowserStore());
        ReviewSession session = new(TestPlans.Preloaded(questions), new UlidGenerator(device), new Random(seed), clock);
        await session.StartAsync(TestContext.Current.CancellationToken);
        return session;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Review_ChoiceKinds_StartWithRecallStep()
    {
        ReviewSession session = await CreateAsync(1, TestQuestions.Single(), TestQuestions.Multi(), TestQuestions.OutputChoice());

        for (int i = 0; i < 3; i++)
        {
            QuestionAttempt attempt = Assert.IsType<QuestionAttempt>(session.Current);
            Assert.True(session.AwaitingRecall);

            // Nothing can be picked while the options are hidden: the step trains recall, not recognition.
            session.Pick(attempt.Options[0].Id);
            Assert.Empty(attempt.Picked);
            Assert.False(session.CanSubmit);

            session.ShowOptions();
            Assert.False(session.AwaitingRecall);
            session.Pick(attempt.Options[0].Id);
            Assert.Single(attempt.Picked);
            Assert.True(session.CanSubmit);
            attempt.SubmitAutomatic();
            await session.NextAsync(Ct);
        }

        Assert.True(session.IsFinished);
        Assert.False(session.AwaitingRecall);
    }

    [Fact]
    public async Task Review_ShowOptions_RevealsShuffled()
    {
        QuestionDto question = TestQuestions.Single();
        string[] original = [.. question.Options.Select(o => o.Id)];

        // Some seed shuffles the four options away from the content order; the attempt keeps that order.
        ReviewSession? session = null;
        for (int seed = 0; seed < 20 && session is null; seed++)
        {
            ReviewSession candidate = await CreateAsync(seed, question);
            if (!candidate.Current!.Options.Select(o => o.Id).SequenceEqual(original, StringComparer.Ordinal))
            {
                session = candidate;
            }
        }

        Assert.NotNull(session);
        string[] shown = [.. session.Current!.Options.Select(o => o.Id)];

        session.ShowOptions();

        Assert.Equal(shown, session.Current!.Options.Select(o => o.Id));
        Assert.Equal(original.Order(StringComparer.Ordinal), shown.Order(StringComparer.Ordinal));
        Assert.NotEqual(original, shown);

        // Showing the options again changes nothing.
        session.ShowOptions();
        Assert.False(session.AwaitingRecall);
        Assert.Equal(shown, session.Current!.Options.Select(o => o.Id));
    }

    [Theory]
    [InlineData("open")]
    [InlineData("situation")]
    [InlineData("find-bug")]
    [InlineData("output-typed")]
    public async Task Review_OpenKind_SkipsRecallStep(string kind)
    {
        QuestionDto question = kind switch
        {
            "find-bug" => TestQuestions.FindBug(),
            "output-typed" => TestQuestions.OutputTyped(),
            _ => TestQuestions.Open(kind),
        };
        ReviewSession session = await CreateAsync(1, question, TestQuestions.Single());

        Assert.False(session.AwaitingRecall);

        // The next choice question starts hidden again.
        await session.NextAsync(Ct);
        Assert.True(session.AwaitingRecall);
    }

    [Fact]
    public async Task Review_Empty_HasNoCurrent()
    {
        ReviewSession session = await CreateAsync(1);

        Assert.True(session.IsEmpty);
        Assert.True(session.IsFinished);
        Assert.Null(session.Current);
        Assert.False(session.AwaitingRecall);
        Assert.Equal(0, session.Count);
        Assert.Empty(session.Results);

        session.ShowOptions();
        await session.NextAsync(Ct);
        Assert.Equal(0, session.Index);
    }
}
