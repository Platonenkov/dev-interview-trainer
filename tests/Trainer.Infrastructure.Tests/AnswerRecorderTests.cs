using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Trainer.Application.Answers;
using Trainer.Domain.Grading;
using Trainer.Domain.Ids;
using Trainer.Domain.Questions;
using Trainer.Domain.Scheduling;
using Trainer.Domain.Users;
using Trainer.Infrastructure.Answers;
using Trainer.Infrastructure.Persistence;
using Trainer.Infrastructure.Persistence.Entities;
using Trainer.TestSupport;

namespace Trainer.Infrastructure.Tests;

public sealed class AnswerRecorderTests(PostgresFixture postgres)
{
    private const long TelegramId = 500_600_700L;
    private const long OtherTelegramId = 500_600_701L;

    private static readonly DateTimeOffset Start = LearningDatabase.Start;
    private static readonly UlidGenerator Ids = new(TimeProvider.System);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClientAnswerId NewId() => new(Ids.Next());

    private static AnswerRecorder CreateRecorder(TrainerDbContext db, TimeProvider clock, ILogger<AnswerRecorder>? logger = null) =>
        new(db, new Sm2ReviewScheduler(), new UlidGenerator(clock), clock, logger ?? NullLogger<AnswerRecorder>.Instance);

    private static ReportedAnswer Answer(
        QuestionId question,
        DateTimeOffset answeredAt,
        Grade grade = Grade.Knew,
        ClientAnswerId? id = null,
        AnswerMode mode = AnswerMode.Practice,
        MockSessionId? mockSessionId = null,
        bool usedHint = false) =>
        new(id ?? NewId(), question, grade, usedHint, mode, mockSessionId, answeredAt);

    private static Task<List<AnswerEntity>> AnswersAsync(TrainerDbContext db) =>
        db.Answers.AsNoTracking().OrderBy(a => a.AnsweredAt).ThenBy(a => a.ClientAnswerId).ToListAsync(Ct);

    private static Task<ReviewStateEntity?> StateAsync(TrainerDbContext db, UserId user, QuestionId question) =>
        db.ReviewStates.AsNoTracking().SingleOrDefaultAsync(r => r.UserId == user.Value && r.QuestionId == question.Value, Ct);

    private static string[] IdsOf(params ReportedAnswer[] answers) => [.. answers.Select(a => a.ClientAnswerId.Value)];

    [Fact]
    public async Task RecordBatch_NewAnswers_StoresRowsAndCreatesReviewStates()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        MockSessionId mock = new(Ids.Next());
        DateTimeOffset answeredAt = Start.AddMinutes(-10);
        ReportedAnswer first = Answer(data.Questions[0], answeredAt, Grade.Knew, mode: AnswerMode.Mock, mockSessionId: mock, usedHint: true);
        ReportedAnswer second = Answer(data.Questions[1], answeredAt.AddMinutes(1), Grade.DidntKnow);
        await using TrainerDbContext db = data.NewContext();

        AnswerBatchResult result = await CreateRecorder(db, data.Clock).RecordAsync(user, [first, second], null, Ct);

        Assert.Equal(IdsOf(first, second), result.Accepted);
        Assert.Empty(result.Rejected);
        List<AnswerEntity> rows = await AnswersAsync(data.Db);
        Assert.Equal(2, rows.Count);
        AnswerEntity row = rows[0];
        Assert.True(UlidText.IsValid(row.Id));
        Assert.NotEqual(first.ClientAnswerId.Value, row.Id);
        Assert.Equal(first.ClientAnswerId.Value, row.ClientAnswerId);
        Assert.Equal(user.Value, row.UserId);
        Assert.Equal(data.Questions[0].Value, row.QuestionId);
        Assert.Equal(Grade.Knew, row.Grade);
        Assert.True(row.UsedHint);
        Assert.Equal(AnswerMode.Mock, row.Mode);
        Assert.Equal(mock.Value, row.MockSessionId);
        Assert.Equal(answeredAt, row.AnsweredAt);
        Assert.Equal(Start, row.ReceivedAt);
        Assert.Null(rows[1].MockSessionId);

        ReviewStateEntity knew = Assert.IsType<ReviewStateEntity>(await StateAsync(data.Db, user, data.Questions[0]));
        Assert.Equal(answeredAt.AddDays(1), knew.DueAt);
        Assert.Equal(1, knew.IntervalDays);
        Assert.Equal(Sm2Constants.InitialEase, knew.Ease);
        Assert.Equal(1, knew.Repetitions);
        Assert.Equal(0, knew.Lapses);
        Assert.Equal(Grade.Knew, knew.LastGrade);
        Assert.Equal(answeredAt, knew.LastAnsweredAt);

        ReviewStateEntity failed = Assert.IsType<ReviewStateEntity>(await StateAsync(data.Db, user, data.Questions[1]));
        Assert.Equal(0, failed.Repetitions);
        Assert.Equal(1, failed.Lapses);
        Assert.Equal(Grade.DidntKnow, failed.LastGrade);
    }

    [Fact]
    public async Task RecordBatch_RetryOfSameBatch_IsIdempotent()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        ReportedAnswer[] batch = [Answer(data.Questions[0], Start.AddMinutes(-2)), Answer(data.Questions[1], Start.AddMinutes(-1))];
        await using TrainerDbContext db = data.NewContext();
        AnswerRecorder recorder = CreateRecorder(db, data.Clock);
        AnswerBatchResult first = await recorder.RecordAsync(user, batch, null, Ct);
        data.Clock.Advance(TimeSpan.FromMinutes(1));

        AnswerBatchResult second = await recorder.RecordAsync(user, batch, null, Ct);

        Assert.Equal(first.Accepted, second.Accepted);
        Assert.Empty(second.Rejected);
        Assert.Equal(2, await data.Db.Answers.CountAsync(Ct));
        ReviewStateEntity state = Assert.IsType<ReviewStateEntity>(await StateAsync(data.Db, user, data.Questions[0]));
        Assert.Equal(1, state.Repetitions);
    }

    [Fact]
    public async Task RecordBatch_SameBatchConcurrently_StoresOnceAndSchedulesOnce()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        ReportedAnswer[] batch = [.. data.Questions.Select((q, i) => Answer(q, Start.AddMinutes(-10 + i)))];
        await using TrainerDbContext firstDb = data.NewContext();
        await using TrainerDbContext secondDb = data.NewContext();
        AnswerRecorder firstRecorder = CreateRecorder(firstDb, data.Clock);
        AnswerRecorder secondRecorder = CreateRecorder(secondDb, data.Clock);
        using Barrier barrier = new(2);

        Task<AnswerBatchResult> a = Task.Run(() =>
        {
            barrier.SignalAndWait(Ct);
            return firstRecorder.RecordAsync(user, batch, null, Ct);
        }, Ct);
        Task<AnswerBatchResult> b = Task.Run(() =>
        {
            barrier.SignalAndWait(Ct);
            return secondRecorder.RecordAsync(user, batch, null, Ct);
        }, Ct);
        AnswerBatchResult[] results = await Task.WhenAll(a, b);

        Assert.All(results, r => Assert.Equal(IdsOf(batch), r.Accepted));
        Assert.Equal(batch.Length, await data.Db.Answers.CountAsync(Ct));
        List<ReviewStateEntity> states = await data.Db.ReviewStates.AsNoTracking().ToListAsync(Ct);
        Assert.Equal(batch.Length, states.Count);
        Assert.All(states, s => Assert.Equal(1, s.Repetitions));
    }

    [Fact]
    public async Task RecordBatch_RetryAfterQuestionRetired_IsAccepted()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        ReportedAnswer answer = Answer(data.Questions[0], Start.AddMinutes(-1));
        await using TrainerDbContext db = data.NewContext();
        AnswerRecorder recorder = CreateRecorder(db, data.Clock);
        await recorder.RecordAsync(user, [answer], null, Ct);
        await data.RetireQuestionAsync(data.Questions[0], Ct);

        ReviewStateEntity before = Assert.IsType<ReviewStateEntity>(await StateAsync(data.Db, user, data.Questions[0]));

        AnswerBatchResult result = await recorder.RecordAsync(user, [answer], null, Ct);

        Assert.Equal(IdsOf(answer), result.Accepted);
        Assert.Empty(result.Rejected);
        Assert.Equal(1, await data.Db.Answers.CountAsync(Ct));
        ReviewStateEntity after = Assert.IsType<ReviewStateEntity>(await StateAsync(data.Db, user, data.Questions[0]));
        Assert.Equal(before.DueAt, after.DueAt);
        Assert.Equal(before.Repetitions, after.Repetitions);
    }

    [Fact]
    public async Task RecordBatch_RetryAfterOneYearPassed_IsAccepted()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        ReportedAnswer answer = Answer(data.Questions[0], Start);
        await using TrainerDbContext db = data.NewContext();
        AnswerRecorder recorder = CreateRecorder(db, data.Clock);
        await recorder.RecordAsync(user, [answer], null, Ct);
        data.Clock.Advance(TimeSpan.FromDays(366));

        AnswerBatchResult result = await recorder.RecordAsync(user, [answer], null, Ct);

        Assert.Equal(IdsOf(answer), result.Accepted);
        Assert.Empty(result.Rejected);
        Assert.Equal(1, await data.Db.Answers.CountAsync(Ct));
    }

    [Fact]
    public async Task RecordBatch_DuplicateIdInsideBatch_StoredOnce()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        ClientAnswerId id = NewId();
        ReportedAnswer first = Answer(data.Questions[0], Start.AddMinutes(-2), Grade.Knew, id);
        ReportedAnswer duplicate = Answer(data.Questions[0], Start.AddMinutes(-1), Grade.DidntKnow, id);
        await using TrainerDbContext db = data.NewContext();

        AnswerBatchResult result = await CreateRecorder(db, data.Clock).RecordAsync(user, [first, duplicate], null, Ct);

        Assert.Equal([id.Value], result.Accepted);
        Assert.Empty(result.Rejected);
        AnswerEntity row = Assert.Single(await AnswersAsync(data.Db));
        Assert.Equal(Grade.Knew, row.Grade);
        ReviewStateEntity state = Assert.IsType<ReviewStateEntity>(await StateAsync(data.Db, user, data.Questions[0]));
        Assert.Equal(0, state.Lapses);
        Assert.Equal(first.AnsweredAt, state.LastAnsweredAt);
    }

    [Fact]
    public async Task RecordBatch_SameIdDifferentPayload_FirstWinsAndLogsIdOnly()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        using CapturingLoggerProvider logs = new();
        using LoggerFactory factory = new([logs]);
        ReportedAnswer original = Answer(data.Questions[0], Start.AddMinutes(-2), Grade.Knew);
        ReportedAnswer changed = original with { QuestionId = data.Questions[1], Grade = Grade.DidntKnow };
        await using TrainerDbContext db = data.NewContext();
        AnswerRecorder recorder = CreateRecorder(db, data.Clock, factory.CreateLogger<AnswerRecorder>());
        await recorder.RecordAsync(user, [original], null, Ct);
        await recorder.RecordAsync(user, [original], null, Ct);
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Warning);

        AnswerBatchResult result = await recorder.RecordAsync(user, [changed], null, Ct);

        Assert.Equal(IdsOf(original), result.Accepted);
        AnswerEntity row = Assert.Single(await AnswersAsync(data.Db));
        Assert.Equal(data.Questions[0].Value, row.QuestionId);
        Assert.Equal(Grade.Knew, row.Grade);
        Assert.Null(await StateAsync(data.Db, user, data.Questions[1]));
        CapturedLogEntry warning = Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal(original.ClientAnswerId.Value, warning.State["ClientAnswerId"]);
        Assert.Equal(["ClientAnswerId", "{OriginalFormat}"], warning.State.Keys.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(data.Questions[0].Value, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(data.Questions[1].Value, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(user.Value, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordBatch_AppliesInAnsweredAtOrder()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        ReportedAnswer older = Answer(data.Questions[0], Start.AddDays(-3), Grade.DidntKnow);
        ReportedAnswer newer = Answer(data.Questions[0], Start.AddDays(-1), Grade.Knew);
        await using TrainerDbContext db = data.NewContext();

        await CreateRecorder(db, data.Clock).RecordAsync(user, [newer, older], null, Ct);

        ReviewStateEntity state = Assert.IsType<ReviewStateEntity>(await StateAsync(data.Db, user, data.Questions[0]));
        Assert.Equal(1, state.Lapses);
        Assert.Equal(1, state.Repetitions);
        Assert.Equal(Grade.Knew, state.LastGrade);
        Assert.Equal(newer.AnsweredAt, state.LastAnsweredAt);
        Assert.Equal(newer.AnsweredAt.AddDays(1), state.DueAt);
    }

    [Fact]
    public async Task RecordBatch_EqualAnsweredAt_AppliesInClientAnswerIdOrder()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        DateTimeOffset at = Start.AddMinutes(-1);
        ClientAnswerId low = new(UlidText.Create(at, new byte[10]));
        ClientAnswerId high = new(UlidText.Create(at, Enumerable.Repeat((byte)0xFF, 10).ToArray()));
        ReportedAnswer failed = Answer(data.Questions[0], at, Grade.DidntKnow, low);
        ReportedAnswer knew = Answer(data.Questions[0], at, Grade.Knew, high);
        await using TrainerDbContext db = data.NewContext();

        await CreateRecorder(db, data.Clock).RecordAsync(user, [knew, failed], null, Ct);

        ReviewStateEntity state = Assert.IsType<ReviewStateEntity>(await StateAsync(data.Db, user, data.Questions[0]));
        Assert.Equal(Grade.DidntKnow, state.LastGrade);
        Assert.Equal(1, state.Lapses);
    }

    [Fact]
    public async Task RecordBatch_OlderAnswerAfterNewer_DoesNotRewindSchedule()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        await using TrainerDbContext db = data.NewContext();
        AnswerRecorder recorder = CreateRecorder(db, data.Clock);
        ReportedAnswer newer = Answer(data.Questions[0], Start.AddHours(-1), Grade.Knew);
        await recorder.RecordAsync(user, [newer], null, Ct);
        ReportedAnswer older = Answer(data.Questions[0], Start.AddHours(-2), Grade.DidntKnow);

        AnswerBatchResult result = await recorder.RecordAsync(user, [older], null, Ct);

        Assert.Equal(IdsOf(older), result.Accepted);
        Assert.Equal(2, await data.Db.Answers.CountAsync(Ct));
        ReviewStateEntity state = Assert.IsType<ReviewStateEntity>(await StateAsync(data.Db, user, data.Questions[0]));
        Assert.Equal(0, state.Lapses);
        Assert.Equal(1, state.Repetitions);
        Assert.Equal(Grade.Knew, state.LastGrade);
        Assert.Equal(newer.AnsweredAt, state.LastAnsweredAt);
    }

    [Fact]
    public async Task RecordBatch_FutureWithinFiveMinutes_ClampedToNow()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        ReportedAnswer answer = Answer(data.Questions[0], Start.AddMinutes(5));
        await using TrainerDbContext db = data.NewContext();

        AnswerBatchResult result = await CreateRecorder(db, data.Clock).RecordAsync(user, [answer], null, Ct);

        Assert.Equal(IdsOf(answer), result.Accepted);
        AnswerEntity row = Assert.Single(await AnswersAsync(data.Db));
        Assert.Equal(Start, row.AnsweredAt);
        ReviewStateEntity state = Assert.IsType<ReviewStateEntity>(await StateAsync(data.Db, user, data.Questions[0]));
        Assert.Equal(Start, state.LastAnsweredAt);
        Assert.Equal(Start.AddDays(1), state.DueAt);
    }

    [Fact]
    public async Task RecordBatch_FutureBeyondFiveMinutes_Rejected()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        ReportedAnswer answer = Answer(data.Questions[0], Start.AddMinutes(5).AddMilliseconds(1));
        await using TrainerDbContext db = data.NewContext();

        AnswerBatchResult result = await CreateRecorder(db, data.Clock).RecordAsync(user, [answer], null, Ct);

        Assert.Empty(result.Accepted);
        RejectedAnswer rejected = Assert.Single(result.Rejected);
        Assert.Equal(new RejectedAnswer(answer.ClientAnswerId.Value, AnswerRejections.AnsweredInFuture), rejected);
        Assert.Equal(0, await data.Db.Answers.CountAsync(Ct));
        Assert.Equal(0, await data.Db.ReviewStates.CountAsync(Ct));
    }

    [Fact]
    public async Task RecordBatch_OlderThanOneYear_Rejected()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        ReportedAnswer answer = Answer(data.Questions[0], Start.AddDays(-365).AddMilliseconds(-1));
        await using TrainerDbContext db = data.NewContext();

        AnswerBatchResult result = await CreateRecorder(db, data.Clock).RecordAsync(user, [answer], null, Ct);

        Assert.Empty(result.Accepted);
        Assert.Equal([new RejectedAnswer(answer.ClientAnswerId.Value, AnswerRejections.AnsweredTooOld)], result.Rejected);
        Assert.Equal(0, await data.Db.Answers.CountAsync(Ct));
        Assert.Equal(0, await data.Db.ReviewStates.CountAsync(Ct));
    }

    [Fact]
    public async Task RecordBatch_ExtremeDates_RejectedWithoutError()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        ReportedAnswer ancient = Answer(data.Questions[0], DateTimeOffset.MinValue);
        ReportedAnswer distant = Answer(data.Questions[1], DateTimeOffset.MaxValue);
        ReportedAnswer valid = Answer(data.Questions[2], Start);
        await using TrainerDbContext db = data.NewContext();

        AnswerBatchResult result = await CreateRecorder(db, data.Clock).RecordAsync(user, [ancient, distant, valid], null, Ct);

        Assert.Equal(IdsOf(valid), result.Accepted);
        Assert.Equal(
            [
                new RejectedAnswer(ancient.ClientAnswerId.Value, AnswerRejections.AnsweredTooOld),
                new RejectedAnswer(distant.ClientAnswerId.Value, AnswerRejections.AnsweredInFuture),
            ],
            result.Rejected);
        Assert.Equal(1, await data.Db.Answers.CountAsync(Ct));
    }

    [Fact]
    public async Task RecordBatch_DeliveredAfterDue_UsesAnsweredAt()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        await using TrainerDbContext db = data.NewContext();
        AnswerRecorder recorder = CreateRecorder(db, data.Clock);
        await recorder.RecordAsync(user, [Answer(data.Questions[0], Start)], null, Ct);
        DateTimeOffset due = Start.AddDays(1);
        data.Clock.SetUtcNow(due.AddHours(2));
        ReportedAnswer early = Answer(data.Questions[0], due.AddMinutes(-1), Grade.Knew);

        AnswerBatchResult result = await recorder.RecordAsync(user, [early], null, Ct);

        Assert.Equal(IdsOf(early), result.Accepted);
        ReviewStateEntity state = Assert.IsType<ReviewStateEntity>(await StateAsync(data.Db, user, data.Questions[0]));
        Assert.Equal(due, state.DueAt);
        Assert.Equal(1, state.IntervalDays);
        Assert.Equal(1, state.Repetitions);
        Assert.Equal(early.AnsweredAt, state.LastAnsweredAt);
    }

    [Fact]
    public async Task RecordBatch_RetryAfterClockMoved_SameStoredTime()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        ReportedAnswer clamped = Answer(data.Questions[0], Start.AddMinutes(3));
        ReportedAnswer plain = Answer(data.Questions[1], Start.AddMinutes(-3));
        using CapturingLoggerProvider logs = new();
        using LoggerFactory factory = new([logs]);
        await using TrainerDbContext db = data.NewContext();
        AnswerRecorder recorder = CreateRecorder(db, data.Clock, factory.CreateLogger<AnswerRecorder>());
        await recorder.RecordAsync(user, [clamped, plain], null, Ct);
        data.Clock.Advance(TimeSpan.FromHours(3));

        AnswerBatchResult result = await recorder.RecordAsync(user, [clamped, plain], null, Ct);

        Assert.Equal(IdsOf(clamped, plain), result.Accepted);
        List<AnswerEntity> rows = await AnswersAsync(data.Db);
        Assert.Equal([Start.AddMinutes(-3), Start], rows.Select(r => r.AnsweredAt));
        Assert.All(rows, r => Assert.Equal(Start, r.ReceivedAt));
        // The clamped original is recognised as the same answer, not as a changed payload.
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Theory]
    [InlineData((short)180)]
    [InlineData((short)840)]
    [InlineData((short)-840)]
    public async Task RecordBatch_StoresClientOffsetOnUser(short offset)
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        await using TrainerDbContext db = data.NewContext();

        await CreateRecorder(db, data.Clock).RecordAsync(user, [Answer(data.Questions[0], Start)], offset, Ct);

        UserEntity row = await data.Db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Value, Ct);
        Assert.Equal(offset, row.UtcOffsetMinutes);
    }

    [Theory]
    [InlineData((short)900)]
    [InlineData((short)-900)]
    [InlineData((short)841)]
    public async Task RecordBatch_InvalidOffsetIgnored(short offset)
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        ReportedAnswer answer = Answer(data.Questions[0], Start);
        await using TrainerDbContext db = data.NewContext();
        AnswerRecorder recorder = CreateRecorder(db, data.Clock);
        await recorder.RecordAsync(user, [Answer(data.Questions[1], Start)], 120, Ct);

        AnswerBatchResult result = await recorder.RecordAsync(user, [answer], offset, Ct);

        Assert.Equal(IdsOf(answer), result.Accepted);
        UserEntity row = await data.Db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Value, Ct);
        Assert.Equal((short)120, row.UtcOffsetMinutes);
    }

    [Fact]
    public async Task RecordBatch_RetiredQuestion_Rejected()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        await data.RetireQuestionAsync(data.Questions[0], Ct);
        ReportedAnswer retired = Answer(data.Questions[0], Start);
        ReportedAnswer live = Answer(data.Questions[1], Start);
        await using TrainerDbContext db = data.NewContext();

        AnswerBatchResult result = await CreateRecorder(db, data.Clock).RecordAsync(user, [retired, live], null, Ct);

        Assert.Equal(IdsOf(live), result.Accepted);
        Assert.Equal([new RejectedAnswer(retired.ClientAnswerId.Value, AnswerRejections.QuestionRetired)], result.Rejected);
        AnswerEntity row = Assert.Single(await AnswersAsync(data.Db));
        Assert.Equal(live.ClientAnswerId.Value, row.ClientAnswerId);
        Assert.Null(await StateAsync(data.Db, user, data.Questions[0]));
    }

    [Fact]
    public async Task RecordBatch_QuestionOfRetiredTopic_Rejected()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        string topic = await data.Db.Questions.AsNoTracking()
            .Where(q => q.Id == data.Questions[0].Value)
            .Select(q => q.TopicKey)
            .SingleAsync(Ct);
        DateTimeOffset now = Start;
        await data.Db.Topics.Where(t => t.Key == topic).ExecuteUpdateAsync(s => s.SetProperty(t => t.RetiredAt, now), Ct);
        ReportedAnswer answer = Answer(data.Questions[0], Start);
        await using TrainerDbContext db = data.NewContext();

        AnswerBatchResult result = await CreateRecorder(db, data.Clock).RecordAsync(user, [answer], null, Ct);

        Assert.Equal([new RejectedAnswer(answer.ClientAnswerId.Value, AnswerRejections.QuestionRetired)], result.Rejected);
        Assert.Equal(0, await data.Db.Answers.CountAsync(Ct));
    }

    [Fact]
    public async Task RecordBatch_UnknownQuestion_Rejected()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        ReportedAnswer unknown = Answer(new QuestionId(Ids.Next()), Start);
        await using TrainerDbContext db = data.NewContext();

        AnswerBatchResult result = await CreateRecorder(db, data.Clock).RecordAsync(user, [unknown], null, Ct);

        Assert.Empty(result.Accepted);
        Assert.Equal([new RejectedAnswer(unknown.ClientAnswerId.Value, AnswerRejections.QuestionUnknown)], result.Rejected);
        Assert.Equal(0, await data.Db.Answers.CountAsync(Ct));
    }

    [Fact]
    public async Task RecordBatch_IdOwnedByAnotherUser_ReportedAcceptedAndUntouched()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId owner = await data.CreateUserAsync(TelegramId, Ct);
        UserId other = await data.CreateUserAsync(OtherTelegramId, Ct);
        ReportedAnswer original = Answer(data.Questions[0], Start.AddMinutes(-1), Grade.Knew);
        ReportedAnswer copy = original with { Grade = Grade.DidntKnow };
        await using TrainerDbContext db = data.NewContext();
        AnswerRecorder recorder = CreateRecorder(db, data.Clock);
        await recorder.RecordAsync(owner, [original], null, Ct);

        AnswerBatchResult result = await recorder.RecordAsync(other, [copy], null, Ct);

        Assert.Equal(IdsOf(original), result.Accepted);
        Assert.Empty(result.Rejected);
        AnswerEntity row = Assert.Single(await AnswersAsync(data.Db));
        Assert.Equal(owner.Value, row.UserId);
        Assert.Equal(Grade.Knew, row.Grade);
        Assert.Null(await StateAsync(data.Db, other, data.Questions[0]));
        ReviewStateEntity ownerState = Assert.IsType<ReviewStateEntity>(await StateAsync(data.Db, owner, data.Questions[0]));
        Assert.Equal(Grade.Knew, ownerState.LastGrade);
    }

    [Fact]
    public async Task RecordBatch_ConcurrencyConflict_IsRetried()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        await using (TrainerDbContext seedDb = data.NewContext())
        {
            await CreateRecorder(seedDb, data.Clock).RecordAsync(user, [Answer(data.Questions[0], Start.AddDays(-2))], null, Ct);
        }

        ConflictingWriteInterceptor interceptor = new(data.ConnectionString, user, data.Questions[0], conflicts: 1);
        await using TrainerDbContext db = CreateContext(data.ConnectionString, interceptor);
        ReportedAnswer answer = Answer(data.Questions[0], Start);

        AnswerBatchResult result = await CreateRecorder(db, data.Clock).RecordAsync(user, [answer], null, Ct);

        Assert.Equal(IdsOf(answer), result.Accepted);
        Assert.Equal(1, interceptor.Conflicts);
        Assert.Equal(2, interceptor.Saves);
        Assert.Equal(2, await data.Db.Answers.CountAsync(Ct));
        ReviewStateEntity state = Assert.IsType<ReviewStateEntity>(await StateAsync(data.Db, user, data.Questions[0]));
        Assert.Equal(2, state.Repetitions);
        Assert.Equal(3, state.IntervalDays);
        Assert.Equal(answer.AnsweredAt, state.LastAnsweredAt);
    }

    [Fact]
    public async Task RecordBatch_PersistentConflict_RethrowsAfterThreeRetries()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        await using (TrainerDbContext seedDb = data.NewContext())
        {
            await CreateRecorder(seedDb, data.Clock).RecordAsync(user, [Answer(data.Questions[0], Start.AddDays(-2))], null, Ct);
        }

        ConflictingWriteInterceptor interceptor = new(data.ConnectionString, user, data.Questions[0], conflicts: int.MaxValue);
        await using TrainerDbContext db = CreateContext(data.ConnectionString, interceptor);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => CreateRecorder(db, data.Clock).RecordAsync(user, [Answer(data.Questions[0], Start)], null, Ct));

        Assert.Equal(4, interceptor.Saves);
        Assert.Equal(1, await data.Db.Answers.CountAsync(Ct));
        ReviewStateEntity state = Assert.IsType<ReviewStateEntity>(await StateAsync(data.Db, user, data.Questions[0]));
        Assert.Equal(1, state.Repetitions);
    }

    [Theory]
    [InlineData(PostgresErrorCodes.DeadlockDetected)]
    [InlineData(PostgresErrorCodes.SerializationFailure)]
    public async Task RecordBatch_DeadlockOrSerializationFailureOnTheCommandPath_IsRetriedAndScheduledOnce(string sqlState)
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        // A real PostgresException raised while the INSERT command runs, i.e. inside EF's execution strategy, which
        // wraps a transient error in an InvalidOperationException (a real cross-user deadlock needs an exact interleaving).
        FailingInsertInterceptor interceptor = new(sqlState);
        await using TrainerDbContext db = CreateContext(data.ConnectionString, interceptor);
        ReportedAnswer answer = Answer(data.Questions[0], Start.AddMinutes(-1));

        AnswerBatchResult result = await CreateRecorder(db, data.Clock).RecordAsync(user, [answer], null, Ct);

        Assert.Equal(IdsOf(answer), result.Accepted);
        Assert.Equal(1, interceptor.Failures);
        Assert.Equal(1, await data.Db.Answers.CountAsync(Ct));
        ReviewStateEntity state = Assert.IsType<ReviewStateEntity>(await StateAsync(data.Db, user, data.Questions[0]));
        Assert.Equal(1, state.Repetitions);
    }

    [Fact]
    public async Task RecordBatch_ForeignIdInsertedBetweenCheckAndSave_RetriesAfterUniqueViolation()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId owner = await data.CreateUserAsync(TelegramId, Ct);
        UserId other = await data.CreateUserAsync(OtherTelegramId, Ct);
        ReportedAnswer original = Answer(data.Questions[0], Start.AddMinutes(-1), Grade.Knew);
        ReportedAnswer copy = original with { Grade = Grade.DidntKnow };
        // The owner's batch commits after this one has checked for stored ids and before it saves, so the insert
        // hits the unique index (23505), not the earlier lookup.
        ScriptedSaveInterceptor interceptor = new(async ct =>
        {
            await using TrainerDbContext ownerDb = data.NewContext();
            await CreateRecorder(ownerDb, data.Clock).RecordAsync(owner, [original], null, ct);
        });
        await using TrainerDbContext db = CreateContext(data.ConnectionString, interceptor);

        AnswerBatchResult result = await CreateRecorder(db, data.Clock).RecordAsync(other, [copy], null, Ct);

        Assert.Equal(IdsOf(original), result.Accepted);
        Assert.Equal(2, interceptor.Saves);
        AnswerEntity row = Assert.Single(await AnswersAsync(data.Db));
        Assert.Equal(owner.Value, row.UserId);
        Assert.Equal(Grade.Knew, row.Grade);
        Assert.Null(await StateAsync(data.Db, other, data.Questions[0]));
    }

    [Fact]
    public async Task RecordBatch_WaitsWhileAnotherSessionHoldsTheUsersAdvisoryLock()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        UserId other = await data.CreateUserAsync(OtherTelegramId, Ct);
        await using NpgsqlConnection holder = new(data.ConnectionString);
        await holder.OpenAsync(Ct);
        await using NpgsqlTransaction hold = await holder.BeginTransactionAsync(Ct);
        await using (NpgsqlCommand take = new("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", holder, hold))
        {
            take.Parameters.AddWithValue("key", AnswerRecorder.LockPrefix + user.Value);
            await take.ExecuteNonQueryAsync(Ct);
        }

        await using TrainerDbContext blockedDb = data.NewContext();
        Task<AnswerBatchResult> blocked = CreateRecorder(blockedDb, data.Clock)
            .RecordAsync(user, [Answer(data.Questions[0], Start.AddMinutes(-1))], null, Ct);
        await using TrainerDbContext freeDb = data.NewContext();
        AnswerBatchResult free = await CreateRecorder(freeDb, data.Clock)
            .RecordAsync(other, [Answer(data.Questions[1], Start.AddMinutes(-1))], null, Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(750), Ct);

        // Another user is not held up; the locked user's batch is.
        Assert.Single(free.Accepted);
        Assert.False(blocked.IsCompleted);
        await hold.CommitAsync(Ct);
        AnswerBatchResult released = await blocked;
        Assert.Single(released.Accepted);
    }

    [Fact]
    public async Task RecordBatch_AfterSuccess_LeavesCallerTrackedEntitiesAttached()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        await using TrainerDbContext db = data.NewContext();
        UserEntity tracked = await db.Users.SingleAsync(u => u.Id == user.Value, Ct);

        await CreateRecorder(db, data.Clock).RecordAsync(user, [Answer(data.Questions[0], Start.AddMinutes(-1))], null, Ct);

        Assert.Equal(EntityState.Unchanged, db.Entry(tracked).State);
    }

    [Fact]
    public async Task RecordBatch_ClampedAnswersToOneQuestion_ApplyInReportedOrder()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        // Both are ahead of the server clock, so both are stored at "now" and the scheduler keeps the first one applied.
        // The client ids sort opposite to the reported times: the reported order decides, not the id.
        ReportedAnswer didntKnow = Answer(data.Questions[0], Start.AddMinutes(1), Grade.DidntKnow, new ClientAnswerId("01JZZZZZZZZZZZZZZZZZZZZZZZ"));
        ReportedAnswer knew = Answer(data.Questions[0], Start.AddMinutes(2), Grade.Knew, new ClientAnswerId("01JZZZZZZZZZZZZZZZZZZZZZZA"));
        await using TrainerDbContext db = data.NewContext();

        AnswerBatchResult result = await CreateRecorder(db, data.Clock).RecordAsync(user, [knew, didntKnow], null, Ct);

        Assert.Equal(2, result.Accepted.Count);
        ReviewStateEntity state = Assert.IsType<ReviewStateEntity>(await StateAsync(data.Db, user, data.Questions[0]));
        Assert.Equal(Grade.DidntKnow, state.LastGrade);
    }

    [Fact]
    public async Task StoredIdLookup_UsesTheUniqueIndex()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        CapturingCommandInterceptor capture = new();
        await using TrainerDbContext db = CreateContext(data.ConnectionString, capture);
        await CreateRecorder(db, data.Clock).RecordAsync(user, [Answer(data.Questions[0], Start.AddMinutes(-1))], null, Ct);
        string lookupText = Assert.IsType<string>(capture.LookupText);

        // Sequential scans are switched off so a plan that cannot use the index (the column cast to text because the
        // parameter was typed text[]) shows as a sequential scan even on this tiny table.
        await using NpgsqlConnection connection = new(data.ConnectionString);
        await connection.OpenAsync(Ct);
        await using (NpgsqlCommand off = new("SET enable_seqscan = off", connection))
        {
            await off.ExecuteNonQueryAsync(Ct);
        }

        await using NpgsqlCommand explain = new("EXPLAIN " + lookupText, connection);
        foreach (NpgsqlParameter parameter in capture.LookupParameters)
        {
            explain.Parameters.Add(parameter);
        }

        List<string> plan = [];
        await using (NpgsqlDataReader reader = await explain.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                plan.Add(reader.GetString(0));
            }
        }

        string text = string.Join('\n', plan);
        Assert.Contains("Index", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Seq Scan", text, StringComparison.Ordinal);
        Assert.Contains("client_answer_id", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordBatch_TooManyAnswers_Throws()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        ReportedAnswer[] batch = [.. Enumerable.Range(0, IAnswerRecorder.MaxBatchSize + 1).Select(_ => Answer(data.Questions[0], Start))];
        await using TrainerDbContext db = data.NewContext();

        await Assert.ThrowsAsync<ArgumentException>(() => CreateRecorder(db, data.Clock).RecordAsync(user, batch, null, Ct));

        Assert.Equal(0, await data.Db.Answers.CountAsync(Ct));
    }

    private static TrainerDbContext CreateContext(string connectionString, IInterceptor interceptor)
    {
        DbContextOptionsBuilder<TrainerDbContext> builder = new();
        TrainerDbContextOptions.Configure(builder, connectionString);
        builder.AddInterceptors(interceptor);
        return new TrainerDbContext(builder.Options);
    }

    /// <summary>
    /// Before the first <paramref name="conflicts"/> saves, rewrites the review-state row from another connection
    /// without changing its values. The rewrite still gives the row a new <c>xmin</c>, so the save that follows
    /// fails its concurrency check.
    /// </summary>
    private sealed class ConflictingWriteInterceptor(string connectionString, UserId user, QuestionId question, int conflicts)
        : SaveChangesInterceptor
    {
        public int Saves { get; private set; }

        public int Conflicts { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Saves++;
            if (Conflicts < conflicts)
            {
                Conflicts++;
                await using NpgsqlConnection connection = new(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using NpgsqlCommand command = connection.CreateCommand();
                command.CommandText = "UPDATE review_states SET lapses = lapses WHERE user_id = @user AND question_id = @question";
                command.Parameters.AddWithValue("user", user.Value);
                command.Parameters.AddWithValue("question", question.Value);
                int updated = await command.ExecuteNonQueryAsync(cancellationToken);
                Assert.Equal(1, updated);
            }

            return result;
        }
    }

    /// <summary>Runs <paramref name="beforeFirstSave"/> before the first save only, then lets every later save through.</summary>
    private sealed class ScriptedSaveInterceptor(Func<CancellationToken, Task> beforeFirstSave) : SaveChangesInterceptor
    {
        public int Saves { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Saves++;
            if (Saves == 1)
            {
                await beforeFirstSave(cancellationToken);
            }

            return result;
        }
    }

    /// <summary>Fails the first <c>INSERT INTO answers</c> with a real <see cref="PostgresException"/> of the given state.</summary>
    private sealed class FailingInsertInterceptor(string sqlState) : DbCommandInterceptor
    {
        public int Failures { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            Fail(command, base.NonQueryExecutingAsync(command, eventData, result, cancellationToken));

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) =>
            Fail(command, base.ReaderExecutingAsync(command, eventData, result, cancellationToken));

        private ValueTask<T> Fail<T>(DbCommand command, ValueTask<T> pass)
        {
            if (Failures == 0 && command.CommandText.Contains("INSERT INTO answers", StringComparison.Ordinal))
            {
                Failures++;
                throw new PostgresException("injected", "ERROR", "ERROR", sqlState);
            }

            return pass;
        }
    }

    /// <summary>Keeps the stored-id lookup command (the only query that filters on <c>client_answer_id</c>).</summary>
    private sealed class CapturingCommandInterceptor : DbCommandInterceptor
    {
        public string? LookupText { get; private set; }

        public List<NpgsqlParameter> LookupParameters { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("client_answer_id", StringComparison.Ordinal)
                && command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                LookupText = command.CommandText;
                LookupParameters.Clear();
                foreach (NpgsqlParameter parameter in command.Parameters.OfType<NpgsqlParameter>())
                {
                    LookupParameters.Add(parameter.Clone());
                }
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
