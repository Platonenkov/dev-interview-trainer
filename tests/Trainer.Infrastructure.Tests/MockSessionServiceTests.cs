using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Trainer.Application.Mock;
using Trainer.Domain.Ids;
using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Users;
using Trainer.Infrastructure.Mock;
using Trainer.Infrastructure.Persistence;
using Trainer.Infrastructure.Persistence.Entities;
using Trainer.TestSupport;

namespace Trainer.Infrastructure.Tests;

public sealed class MockSessionServiceTests(PostgresFixture postgres)
{
    private const long TelegramId = 800_900_100L;
    private const long OtherTelegramId = 800_900_101L;

    private static readonly DateTimeOffset Start = LearningDatabase.Start;
    private static readonly UlidGenerator Ids = new(TimeProvider.System);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MockSummary Summary(
        DateTimeOffset? startedAt = null,
        DateTimeOffset? finishedAt = null,
        Level level = Level.Middle,
        int questionCount = 20,
        bool timed = true,
        params MockTopicResult[] topics) =>
        new(
            new StackKey("dotnet"),
            level,
            questionCount,
            timed,
            startedAt ?? Start.AddMinutes(-30),
            finishedAt ?? Start.AddMinutes(-1),
            topics.Length == 0 ? [new MockTopicResult(new TopicKey("async"), 3, 1, 2, 0)] : topics);

    private static Task<MockSessionEntity?> RowAsync(TrainerDbContext db, MockSessionId id) =>
        db.MockSessions.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id.Value, Ct);

    [Fact]
    public async Task MockSave_CreatesThenUpdatesSameId()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        MockSessionId id = new(Ids.Next());
        await using TrainerDbContext db = data.NewContext();
        MockSessionService service = new(db, data.Clock);

        MockSaveResult created = await service.SaveAsync(user, id, Summary(), Ct);

        Assert.Equal(MockSaveResult.Created, created);
        MockSessionEntity first = Assert.IsType<MockSessionEntity>(await RowAsync(data.Db, id));
        Assert.Equal(user.Value, first.UserId);
        Assert.Equal("dotnet", first.StackKey);
        Assert.Equal(Level.Middle, first.Level);
        Assert.Equal(20, first.QuestionCount);
        Assert.True(first.Timed);
        Assert.Equal(Start.AddMinutes(-30), first.StartedAt);
        Assert.Equal(Start.AddMinutes(-1), first.FinishedAt);

        MockSummary changed = Summary(
            finishedAt: Start,
            level: Level.Senior,
            questionCount: 10,
            timed: false,
            topics: [new MockTopicResult(new TopicKey("csharp"), 1, 2, 3, 4), new MockTopicResult(new TopicKey("async"), 0, 0, 0, 1)]);
        MockSaveResult updated = await service.SaveAsync(user, id, changed, Ct);

        Assert.Equal(MockSaveResult.Updated, updated);
        MockSessionEntity second = Assert.IsType<MockSessionEntity>(await RowAsync(data.Db, id));
        Assert.Equal(Level.Senior, second.Level);
        Assert.Equal(10, second.QuestionCount);
        Assert.False(second.Timed);
        Assert.Equal(Start, second.FinishedAt);
        using JsonDocument summary = JsonDocument.Parse(second.Summary);
        JsonElement[] topics = [.. summary.RootElement.GetProperty("topics").EnumerateArray()];
        Assert.Equal(2, topics.Length);
        Assert.Equal("csharp", topics[0].GetProperty("topic").GetString());
        Assert.Equal(1, topics[0].GetProperty("knew").GetInt32());
        Assert.Equal(2, topics[0].GetProperty("partial").GetInt32());
        Assert.Equal(3, topics[0].GetProperty("didntKnow").GetInt32());
        Assert.Equal(4, topics[0].GetProperty("unanswered").GetInt32());
        Assert.Equal(1, await data.Db.MockSessions.CountAsync(Ct));
    }

    [Fact]
    public async Task MockSave_OtherUsersId_NotFound()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId owner = await data.CreateUserAsync(TelegramId, Ct);
        UserId other = await data.CreateUserAsync(OtherTelegramId, Ct);
        MockSessionId id = new(Ids.Next());
        await using TrainerDbContext db = data.NewContext();
        MockSessionService service = new(db, data.Clock);
        await service.SaveAsync(owner, id, Summary(), Ct);

        MockSaveResult result = await service.SaveAsync(other, id, Summary(level: Level.Lead), Ct);

        Assert.Equal(MockSaveResult.NotFound, result);
        MockSessionEntity row = Assert.IsType<MockSessionEntity>(await RowAsync(data.Db, id));
        Assert.Equal(owner.Value, row.UserId);
        Assert.Equal(Level.Middle, row.Level);
    }

    [Fact]
    public async Task MockSave_FinishedTooOld_Invalid()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        MockSessionId id = new(Ids.Next());
        await using TrainerDbContext db = data.NewContext();
        DateTimeOffset tooOld = Start.AddDays(-365).AddMilliseconds(-1);

        MockSaveResult result = await new MockSessionService(db, data.Clock).SaveAsync(
            user, id, Summary(startedAt: tooOld.AddMinutes(-30), finishedAt: tooOld), Ct);

        Assert.Equal(MockSaveResult.Invalid, result);
        Assert.Null(await RowAsync(data.Db, id));
    }

    [Fact]
    public async Task MockSave_StartedTooOld_Invalid()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        MockSessionId id = new(Ids.Next());
        await using TrainerDbContext db = data.NewContext();

        MockSaveResult result = await new MockSessionService(db, data.Clock).SaveAsync(
            user, id, Summary(startedAt: DateTimeOffset.MinValue), Ct);

        Assert.Equal(MockSaveResult.Invalid, result);
        Assert.Null(await RowAsync(data.Db, id));
    }

    [Fact]
    public async Task MockSave_FinishedBeyondTolerance_Invalid()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        MockSessionId id = new(Ids.Next());
        await using TrainerDbContext db = data.NewContext();
        MockSessionService service = new(db, data.Clock);

        Assert.Equal(MockSaveResult.Invalid, await service.SaveAsync(user, id, Summary(finishedAt: Start.AddMinutes(6)), Ct));
        Assert.Equal(MockSaveResult.Invalid, await service.SaveAsync(user, id, Summary(finishedAt: DateTimeOffset.MaxValue), Ct));
        Assert.Null(await RowAsync(data.Db, id));
    }

    [Fact]
    public async Task MockSave_FinishedWithinTolerance_ClampedToNow()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        MockSessionId id = new(Ids.Next());
        await using TrainerDbContext db = data.NewContext();

        MockSaveResult result = await new MockSessionService(db, data.Clock).SaveAsync(
            user, id, Summary(finishedAt: Start.AddMinutes(4)), Ct);

        Assert.Equal(MockSaveResult.Created, result);
        MockSessionEntity row = Assert.IsType<MockSessionEntity>(await RowAsync(data.Db, id));
        Assert.Equal(Start, row.FinishedAt);
    }

    [Fact]
    public async Task MockSave_FinishedBeforeStarted_Invalid()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        MockSessionId id = new(Ids.Next());
        await using TrainerDbContext db = data.NewContext();

        MockSaveResult result = await new MockSessionService(db, data.Clock).SaveAsync(
            user, id, Summary(startedAt: Start.AddMinutes(-1), finishedAt: Start.AddMinutes(-2)), Ct);

        Assert.Equal(MockSaveResult.Invalid, result);
        Assert.Null(await RowAsync(data.Db, id));
    }

    [Fact]
    public async Task MockSave_ReplayOfIdenticalSummary_UpdatesAndLeavesTheRowUnchanged()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        MockSessionId id = new(Ids.Next());
        MockSummary summary = Summary();
        await using TrainerDbContext db = data.NewContext();
        MockSessionService service = new(db, data.Clock);
        await service.SaveAsync(user, id, summary, Ct);
        MockSessionEntity before = Assert.IsType<MockSessionEntity>(await RowAsync(data.Db, id));

        MockSaveResult replay = await service.SaveAsync(user, id, summary, Ct);

        Assert.Equal(MockSaveResult.Updated, replay);
        Assert.Equal(1, await data.Db.MockSessions.CountAsync(Ct));
        MockSessionEntity after = Assert.IsType<MockSessionEntity>(await RowAsync(data.Db, id));
        Assert.Equal(before.StackKey, after.StackKey);
        Assert.Equal(before.Level, after.Level);
        Assert.Equal(before.QuestionCount, after.QuestionCount);
        Assert.Equal(before.StartedAt, after.StartedAt);
        Assert.Equal(before.FinishedAt, after.FinishedAt);
        Assert.Equal(before.Summary, after.Summary);
    }

    [Fact]
    public async Task MockSave_ParallelSaveInsertsBetweenUpdateAndInsert_ThirdStepUpdates()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        MockSessionId id = new(Ids.Next());
        MockSummary first = Summary(questionCount: 10);
        MockSummary second = Summary(questionCount: 12);
        // The same user's other tab creates the session after this save found nothing to update and before its own
        // insert runs, so the insert does nothing and the final update must replace the other tab's row.
        InsertBeforeInsertInterceptor interceptor = new(async ct =>
        {
            await using TrainerDbContext otherDb = data.NewContext();
            MockSaveResult created = await new MockSessionService(otherDb, data.Clock).SaveAsync(user, id, first, ct);
            Assert.Equal(MockSaveResult.Created, created);
        });
        DbContextOptionsBuilder<TrainerDbContext> options = new();
        TrainerDbContextOptions.Configure(options, data.ConnectionString);
        options.AddInterceptors(interceptor);
        await using TrainerDbContext db = new(options.Options);

        MockSaveResult result = await new MockSessionService(db, data.Clock).SaveAsync(user, id, second, Ct);

        Assert.Equal(MockSaveResult.Updated, result);
        MockSessionEntity row = Assert.IsType<MockSessionEntity>(await RowAsync(data.Db, id));
        Assert.Equal(12, row.QuestionCount);
        Assert.Equal(user.Value, row.UserId);
    }

    /// <summary>Runs <paramref name="beforeInsert"/> once, right before the service's <c>INSERT INTO mock_sessions</c>.</summary>
    private sealed class InsertBeforeInsertInterceptor(Func<CancellationToken, Task> beforeInsert) : DbCommandInterceptor
    {
        private int _fired;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO mock_sessions", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await beforeInsert(cancellationToken);
            }

            return result;
        }
    }
}
