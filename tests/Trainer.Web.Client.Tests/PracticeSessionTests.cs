using Microsoft.Extensions.Time.Testing;
using Trainer.Contracts;
using Trainer.Domain.Grading;
using Trainer.Domain.Ids;
using Trainer.Web.Client.Api;
using Trainer.Web.Client.Sessions;

namespace Trainer.Web.Client.Tests;

public sealed class PracticeSessionTests
{
    private static readonly DateTimeOffset DeviceStart = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (PracticeSession Session, ServerClock Clock, FakeTimeProvider Device) Create(params QuestionDto[] questions)
    {
        FakeTimeProvider device = new(DeviceStart);
        ServerClock clock = new(device, new FakeBrowserStore());
        return (new PracticeSession(questions, new UlidGenerator(device), new Random(3), clock), clock, device);
    }

    [Fact]
    public void Session_Empty_HasNoCurrentAndIsFinished()
    {
        PracticeSession session = Create().Session;

        Assert.True(session.IsEmpty);
        Assert.True(session.IsFinished);
        Assert.Null(session.Current);
        Assert.Equal(0, session.Count);
        Assert.Empty(session.Results);
        Assert.Empty(session.SeenIds);

        session.Next();
        Assert.Equal(0, session.Index);
    }

    [Fact]
    public void Session_NextAdvancesAndFinishes()
    {
        QuestionDto single = TestQuestions.Single(correct: "a");
        QuestionDto open = TestQuestions.Open();
        QuestionDto multi = TestQuestions.Multi();
        PracticeSession session = Create(single, open, multi).Session;

        Assert.False(session.IsEmpty);
        Assert.Equal(3, session.Count);
        QuestionAttempt first = Assert.IsType<QuestionAttempt>(session.Current);
        Assert.Same(single, first.Question);
        Assert.Same(first, session.Current);
        Assert.True(UlidText.IsValid(first.ClientAnswerId));
        first.Toggle("a");
        first.SubmitAutomatic();

        session.Next();
        QuestionAttempt second = Assert.IsType<QuestionAttempt>(session.Current);
        Assert.Same(open, second.Question);
        Assert.NotEqual(first.ClientAnswerId, second.ClientAnswerId);
        second.RevealReference();
        second.Skip();

        session.Next();
        QuestionAttempt third = Assert.IsType<QuestionAttempt>(session.Current);
        third.RevealReference();
        Assert.Equal(2, session.Index);
        Assert.False(session.IsFinished);

        session.Next();
        Assert.True(session.IsFinished);
        Assert.Null(session.Current);
        Assert.Equal(3, session.Index);
        session.Next();
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
        PracticeSession session = Create(open, TestQuestions.Single()).Session;
        QuestionAttempt current = session.Current!;
        current.RevealReference();
        await current.SubmitSelfAssessmentAsync(Grade.Partial, new SelfAssessmentGrader(), Ct);

        Assert.Equal([(open, Grade.Partial)], session.Results);
        Assert.Equal([open.Id], session.SeenIds);
    }

    [Fact]
    public void Session_DuplicateQuestion_IsSeenOnce()
    {
        QuestionDto single = TestQuestions.Single();
        PracticeSession session = Create(single, single).Session;
        session.Next();

        Assert.Equal([single.Id], session.SeenIds);
    }

    [Fact]
    public void AnswerFactory_UsesAttemptIdAndTime()
    {
        (PracticeSession session, ServerClock clock, FakeTimeProvider device) = Create(TestQuestions.Single(correct: "b"));
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
    public void AnswerFactory_RejectsUngradedAttemptsAndMismatchedMockId()
    {
        PracticeSession session = Create(TestQuestions.Single()).Session;
        QuestionAttempt attempt = session.Current!;
        AnswerFactory factory = new();

        Assert.Throws<InvalidOperationException>(() => factory.Create(attempt, AnswerMode.Practice, null));

        attempt.Toggle("a");
        attempt.SubmitAutomatic();
        Assert.Throws<ArgumentException>(() => factory.Create(attempt, AnswerMode.Mock, null));
        Assert.Throws<ArgumentException>(() => factory.Create(attempt, AnswerMode.Review, "01JC0000000000000000000000"));
    }
}
