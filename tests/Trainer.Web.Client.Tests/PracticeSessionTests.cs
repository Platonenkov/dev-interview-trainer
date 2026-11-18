using Microsoft.Extensions.Time.Testing;
using Trainer.Contracts;
using Trainer.Domain.Grading;
using Trainer.Domain.Ids;
using Trainer.Web.Client.Api;
using Trainer.Web.Client.Backend;
using Trainer.Web.Client.Sessions;

namespace Trainer.Web.Client.Tests;

public sealed class PracticeSessionTests
{
    private static readonly DateTimeOffset DeviceStart = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PracticeSession NewSession(SessionPlan plan, FakeTimeProvider device, out ServerClock clock)
    {
        clock = new ServerClock(device, new FakeBrowserStore());
        return new PracticeSession(plan, new UlidGenerator(device), new Random(3), clock);
    }

    private static async Task<(PracticeSession Session, ServerClock Clock, FakeTimeProvider Device)> CreateAsync(params QuestionDto[] questions)
    {
        FakeTimeProvider device = new(DeviceStart);
        PracticeSession session = NewSession(TestPlans.Preloaded(questions), device, out ServerClock clock);
        await session.StartAsync(Ct);
        return (session, clock, device);
    }

    [Fact]
    public async Task Session_Empty_HasNoCurrentAndIsFinished()
    {
        PracticeSession session = (await CreateAsync()).Session;

        Assert.True(session.IsEmpty);
        Assert.True(session.IsFinished);
        Assert.Null(session.Current);
        Assert.Equal(0, session.Count);
        Assert.Empty(session.Results);
        Assert.Empty(session.SeenIds);

        await session.NextAsync(Ct);
        Assert.Equal(0, session.Index);
    }

    [Fact]
    public async Task Session_NextAdvancesAndFinishes()
    {
        QuestionDto single = TestQuestions.Single(correct: "a");
        QuestionDto open = TestQuestions.Open();
        QuestionDto multi = TestQuestions.Multi();
        PracticeSession session = (await CreateAsync(single, open, multi)).Session;

        Assert.False(session.IsEmpty);
        Assert.Equal(3, session.Count);
        QuestionAttempt first = Assert.IsType<QuestionAttempt>(session.Current);
        Assert.Same(single, first.Question);
        Assert.Same(first, session.Current);
        Assert.True(UlidText.IsValid(first.ClientAnswerId));
        first.Toggle("a");
        first.SubmitAutomatic();

        await session.NextAsync(Ct);
        QuestionAttempt second = Assert.IsType<QuestionAttempt>(session.Current);
        Assert.Same(open, second.Question);
        Assert.NotEqual(first.ClientAnswerId, second.ClientAnswerId);
        second.RevealReference();
        second.Skip();

        await session.NextAsync(Ct);
        QuestionAttempt third = Assert.IsType<QuestionAttempt>(session.Current);
        third.RevealReference();
        Assert.Equal(2, session.Index);
        Assert.False(session.IsFinished);

        await session.NextAsync(Ct);
        Assert.True(session.IsFinished);
        Assert.Null(session.Current);
        Assert.Equal(3, session.Index);
        await session.NextAsync(Ct);
        Assert.Equal(3, session.Index);

        // A skipped or unanswered question has no result; every shown question counts as seen.
        (QuestionDto Question, Grade Grade) result = Assert.Single(session.Results);
        Assert.Same(single, result.Question);
        Assert.Equal(Grade.Knew, result.Grade);
        Assert.Equal([single.Id, open.Id, multi.Id], session.SeenIds);
    }

    [Fact]
    public async Task Session_Results_IncludeTheGradedCurrentQuestion()
    {
        QuestionDto open = TestQuestions.Open();
        PracticeSession session = (await CreateAsync(open, TestQuestions.Single())).Session;
        QuestionAttempt current = session.Current!;
        current.RevealReference();
        await current.SubmitSelfAssessmentAsync(Grade.Partial, new SelfAssessmentGrader(), Ct);

        Assert.Equal([(open, Grade.Partial)], session.Results);
        Assert.Equal([open.Id], session.SeenIds);
    }

    [Fact]
    public async Task Session_DuplicateQuestion_IsSeenOnce()
    {
        QuestionDto single = TestQuestions.Single();
        PracticeSession session = (await CreateAsync(single, single)).Session;
        await session.NextAsync(Ct);

        Assert.Equal([single.Id], session.SeenIds);
    }

    [Fact]
    public async Task AnswerFactory_UsesAttemptIdAndTime()
    {
        (PracticeSession session, ServerClock clock, FakeTimeProvider device) = await CreateAsync(TestQuestions.Single(correct: "b"));
        QuestionAttempt attempt = session.Current!;
        attempt.RevealHint();
        attempt.Toggle("b");
        attempt.SubmitAutomatic();
        DateTimeOffset answeredAt = clock.Now();
        device.Advance(TimeSpan.FromMinutes(10));
        AnswerFactory factory = new();

        AnswerDto practice = factory.Create(attempt, AnswerMode.Practice, null);
        AnswerDto again = factory.Create(attempt, AnswerMode.Practice, null);
        AnswerDto mock = factory.Create(attempt, AnswerMode.Mock, "01JC0000000000000000000000");

        Assert.Equal(attempt.ClientAnswerId, practice.ClientAnswerId);
        Assert.Equal(practice, again);
        Assert.Equal(attempt.Question.Id, practice.QuestionId);
        Assert.Equal("partial", practice.Grade);
        Assert.True(practice.UsedHint);
        Assert.Equal("practice", practice.Mode);
        Assert.Null(practice.MockSessionId);
        Assert.Equal(answeredAt, practice.AnsweredAt);
        Assert.Equal("mock", mock.Mode);
        Assert.Equal("01JC0000000000000000000000", mock.MockSessionId);
    }

    [Fact]
    public async Task AnswerFactory_RejectsUngradedAttemptsAndMismatchedMockId()
    {
        PracticeSession session = (await CreateAsync(TestQuestions.Single())).Session;
        QuestionAttempt attempt = session.Current!;
        AnswerFactory factory = new();

        Assert.Throws<InvalidOperationException>(() => factory.Create(attempt, AnswerMode.Practice, null));

        attempt.Toggle("a");
        attempt.SubmitAutomatic();
        Assert.Throws<ArgumentException>(() => factory.Create(attempt, AnswerMode.Mock, null));
        Assert.Throws<ArgumentException>(() => factory.Create(attempt, AnswerMode.Review, "01JC0000000000000000000000"));
    }

    [Fact]
    public async Task Start_ShowsTheFirstQuestionAndPrefetchesTheSecond()
    {
        QuestionDto q1 = TestQuestions.Single();
        QuestionDto q2 = TestQuestions.Open();
        RecordingQuestionSource source = new([q1, q2]);
        PracticeSession session = NewSession(source.Plan(), new FakeTimeProvider(DeviceStart), out _);

        Assert.Null(session.Current);
        ApiResult<QuestionDto> started = await session.StartAsync(Ct);

        Assert.True(started.IsSuccess);
        Assert.Same(q1, session.Current!.Question);
        Assert.Equal(0, session.Index);
        Assert.Equal([$"get:{q1.Id}", $"prefetch:{q2.Id}"], source.Calls);
    }

    [Fact]
    public async Task Next_PrefetchesTheFollowingQuestion_ButNothingAfterTheLast()
    {
        QuestionDto q1 = TestQuestions.Single();
        QuestionDto q2 = TestQuestions.Open();
        QuestionDto q3 = TestQuestions.Multi();
        RecordingQuestionSource source = new([q1, q2, q3]);
        PracticeSession session = NewSession(source.Plan(), new FakeTimeProvider(DeviceStart), out _);
        await session.StartAsync(Ct);
        source.Calls.Clear();

        await session.NextAsync(Ct);
        await session.NextAsync(Ct);

        Assert.Equal([$"get:{q2.Id}", $"prefetch:{q3.Id}", $"get:{q3.Id}"], source.Calls);
        Assert.Same(q3, session.Current!.Question);
    }

    [Fact]
    public async Task Next_AwaitsAQuestionStillLoading_CurrentIsNullMeanwhile()
    {
        QuestionDto q1 = TestQuestions.Single();
        QuestionDto q2 = TestQuestions.Open();
        RecordingQuestionSource source = new([q1, q2]);
        TaskCompletionSource<ApiResult<QuestionDto>> gate = source.Hold(q2.Id);
        PracticeSession session = NewSession(source.Plan(), new FakeTimeProvider(DeviceStart), out _);
        await session.StartAsync(Ct);

        Task<ApiResult<QuestionDto>> next = session.NextAsync(Ct);

        Assert.False(next.IsCompleted);
        Assert.Null(session.Current);
        Assert.False(session.IsFinished);
        gate.SetResult(new ApiResult<QuestionDto>(q2, 200, null));
        Assert.True((await next).IsSuccess);
        Assert.Same(q2, session.Current!.Question);
        Assert.Equal(1, session.Index);
    }

    [Fact]
    public async Task FailedLoad_KeepsTheIndexAndResultsAndRetries()
    {
        QuestionDto q1 = TestQuestions.Single(correct: "a");
        QuestionDto q2 = TestQuestions.Open();
        RecordingQuestionSource source = new([q1, q2]);
        source.Hold(q2.Id).SetResult(new ApiResult<QuestionDto>(null, 503, null));
        PracticeSession session = NewSession(source.Plan(), new FakeTimeProvider(DeviceStart), out _);
        await session.StartAsync(Ct);
        QuestionAttempt first = session.Current!;
        first.Toggle("a");
        first.SubmitAutomatic();

        ApiResult<QuestionDto> failed = await session.NextAsync(Ct);

        Assert.Equal(503, failed.Status);
        Assert.Equal(0, session.Index);
        Assert.Equal([(q1, Grade.Knew)], session.Results);
        Assert.Equal([q1.Id], session.SeenIds);
        Assert.False(session.IsFinished);

        ApiResult<QuestionDto> retried = await session.NextAsync(Ct);

        Assert.True(retried.IsSuccess);
        Assert.Equal(1, session.Index);
        Assert.Same(q2, session.Current!.Question);
        Assert.Equal([(q1, Grade.Knew)], session.Results);
        Assert.Equal([q1.Id, q2.Id], session.SeenIds);
    }

    [Fact]
    public async Task FailedStart_KeepsTheIndexAndRetriesTheFirstQuestion()
    {
        QuestionDto q1 = TestQuestions.Single();
        RecordingQuestionSource source = new([q1]);
        source.Hold(q1.Id).SetResult(new ApiResult<QuestionDto>(null, 0, null));
        PracticeSession session = NewSession(source.Plan(), new FakeTimeProvider(DeviceStart), out _);

        ApiResult<QuestionDto> failed = await session.StartAsync(Ct);

        Assert.Equal(0, failed.Status);
        Assert.Null(session.Current);
        Assert.Equal(0, session.Index);
        Assert.False(session.IsFinished);

        Assert.True((await session.StartAsync(Ct)).IsSuccess);
        Assert.Same(q1, session.Current!.Question);
    }
}
