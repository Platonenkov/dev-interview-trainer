using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using System.Globalization;
using Trainer.Application.Home;
using Trainer.Application.Progress;
using Trainer.Contracts;
using Trainer.Domain.Grading;
using Trainer.Domain.Ids;
using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;
using Trainer.Domain.Users;
using Trainer.Infrastructure.Persistence;
using Trainer.Infrastructure.Persistence.Entities;
using Trainer.Infrastructure.Progress;
using Trainer.Infrastructure.Queries;
using Trainer.TestSupport;

namespace Trainer.Infrastructure.Tests;

public sealed class ProgressQueriesTests(PostgresFixture postgres)
{
    private const long TelegramId = 610_000_001L;
    private const long OtherTelegramId = 610_000_002L;

    private static readonly CultureKey En = new("en");
    private static readonly CultureKey Ru = new("ru");
    private static readonly StackKey Dotnet = new("dotnet");
    private static readonly UlidGenerator Ids = new(TimeProvider.System);

    // The six imported fixture questions, in id order: csharp/junior, async/middle x2, concurrency/middle x2 and senior.
    private const int Closure = 0;
    private const int ConfigureAwait = 1;
    private const int ResultDeadlock = 2;
    private const int ThreadSafe = 3;
    private const int ParallelRace = 4;
    private const int Starvation = 5;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ProgressQueries CreateQueries(TrainerDbContext db, LearningDatabase data) => new(db, data.Clock);

    private static async Task AddStateAsync(
        LearningDatabase data, UserId user, QuestionId question, TimeSpan dueIn, Grade grade = Grade.Knew)
    {
        data.Db.ReviewStates.Add(new ReviewStateEntity
        {
            UserId = user.Value,
            QuestionId = question.Value,
            DueAt = data.Clock.GetUtcNow() + dueIn,
            IntervalDays = 1,
            Ease = 2.5m,
            Repetitions = 1,
            Lapses = 0,
            LastGrade = grade,
            LastAnsweredAt = data.Clock.GetUtcNow().AddDays(-1),
        });
        await data.Db.SaveChangesAsync(Ct);
        data.Db.ChangeTracker.Clear();
    }

    private static async Task AddAnswerAsync(LearningDatabase data, UserId user, DateTimeOffset localTime)
    {
        // Npgsql writes timestamptz only from offset zero; the instant is what counts.
        DateTimeOffset answeredAt = localTime.ToUniversalTime();
        data.Db.Answers.Add(new AnswerEntity
        {
            Id = Ids.Next(),
            ClientAnswerId = Ids.Next(),
            UserId = user.Value,
            QuestionId = data.Questions[Closure].Value,
            Grade = Grade.Knew,
            Mode = AnswerMode.Practice,
            AnsweredAt = answeredAt,
            ReceivedAt = answeredAt,
        });
        await data.Db.SaveChangesAsync(Ct);
        data.Db.ChangeTracker.Clear();
    }

    private static async Task SetOffsetAsync(LearningDatabase data, UserId user, short minutes)
    {
        string id = user.Value;
        await data.Db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.UtcOffsetMinutes, minutes), Ct);
    }

    private static async Task AddQuestionAsync(LearningDatabase data, string id, bool russian)
    {
        data.Db.Questions.Add(new QuestionEntity
        {
            Id = id,
            StackKey = "dotnet",
            TopicKey = "async",
            Slug = "extra-" + id.ToLowerInvariant(),
            Level = Level.Junior,
            Type = QuestionType.Open,
            Grading = GradingMode.Self,
            Verified = new DateOnly(2026, 11, 11),
        });
        foreach ((bool present, string culture) in new[] { (true, "en"), (russian, "ru") })
        {
            if (present)
            {
                data.Db.QuestionLocales.Add(new QuestionLocaleEntity
                {
                    QuestionId = id,
                    Culture = culture,
                    Title = "Extra " + id,
                    PromptHtml = "<p>p</p>",
                    AnswerHtml = "<p>a</p>",
                    ExplanationHtml = "<p>e</p>",
                    Options = "[]",
                    Links = "[]",
                });
            }
        }

        await data.Db.SaveChangesAsync(Ct);
        data.Db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Statuses_ReturnsOnlyAnsweredWithDueFlag()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        UserId other = await data.CreateUserAsync(OtherTelegramId, Ct);
        await AddStateAsync(data, user, data.Questions[ConfigureAwait], TimeSpan.FromHours(-1), Grade.Knew);
        await AddStateAsync(data, user, data.Questions[ResultDeadlock], TimeSpan.FromDays(3), Grade.Partial);
        await AddStateAsync(data, user, data.Questions[ThreadSafe], TimeSpan.Zero, Grade.DidntKnow);
        await AddStateAsync(data, other, data.Questions[Closure], TimeSpan.FromDays(1));
        await using TrainerDbContext db = data.NewContext();

        IReadOnlyDictionary<QuestionId, QuestionStatus> statuses = await CreateQueries(db, data)
            .GetStatusesAsync(user, data.Questions, Ct);

        Assert.Equal(3, statuses.Count);
        Assert.False(statuses.ContainsKey(data.Questions[Closure]));
        QuestionStatus overdue = statuses[data.Questions[ConfigureAwait]];
        Assert.Equal(Grade.Knew, overdue.LastGrade);
        Assert.True(overdue.IsDue);
        QuestionStatus later = statuses[data.Questions[ResultDeadlock]];
        Assert.Equal(Grade.Partial, later.LastGrade);
        Assert.False(later.IsDue);
        Assert.Equal(data.Clock.GetUtcNow().AddDays(3), later.DueAt);
        Assert.True(statuses[data.Questions[ThreadSafe]].IsDue, "A review due exactly now is due.");
    }

    [Fact]
    public async Task Statuses_OnlyAsksAboutTheGivenIds()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        await AddStateAsync(data, user, data.Questions[ConfigureAwait], TimeSpan.FromDays(1));
        await AddStateAsync(data, user, data.Questions[Closure], TimeSpan.FromDays(1));
        await using TrainerDbContext db = data.NewContext();
        ProgressQueries queries = CreateQueries(db, data);

        IReadOnlyDictionary<QuestionId, QuestionStatus> one = await queries.GetStatusesAsync(user, [data.Questions[Closure]], Ct);
        IReadOnlyDictionary<QuestionId, QuestionStatus> none = await queries.GetStatusesAsync(user, [], Ct);

        Assert.Equal(data.Questions[Closure], Assert.Single(one.Keys));
        Assert.Empty(none);
    }

    [Fact]
    public async Task DueCount_CountsLiveInCultureOnly()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        UserId other = await data.CreateUserAsync(OtherTelegramId, Ct);
        await AddQuestionAsync(data, "01KBZ3Q8N9V2K3M4P5R6S7T8X1", russian: false);
        QuestionId englishOnly = new("01KBZ3Q8N9V2K3M4P5R6S7T8X1");
        await AddStateAsync(data, user, data.Questions[Closure], TimeSpan.FromDays(-1));
        await AddStateAsync(data, user, data.Questions[ConfigureAwait], TimeSpan.Zero);
        await AddStateAsync(data, user, data.Questions[ResultDeadlock], TimeSpan.FromMinutes(1));
        await AddStateAsync(data, user, data.Questions[Starvation], TimeSpan.FromDays(-2));
        await AddStateAsync(data, user, englishOnly, TimeSpan.FromDays(-1));
        await AddStateAsync(data, other, data.Questions[ThreadSafe], TimeSpan.FromDays(-1));
        await data.RetireQuestionAsync(data.Questions[Starvation], Ct);
        await using TrainerDbContext db = data.NewContext();
        ProgressQueries queries = CreateQueries(db, data);

        int ru = await queries.GetDueCountAsync(user, Ru, Dotnet, Ct);
        int en = await queries.GetDueCountAsync(user, En, Dotnet, Ct);
        int unknownStack = await queries.GetDueCountAsync(user, En, new StackKey("java"), Ct);

        Assert.Equal(2, ru);
        Assert.Equal(3, en);
        Assert.Equal(0, unknownStack);
    }

    [Fact]
    public async Task Profile_AggregatesByTopicAndLevel()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        await AddStateAsync(data, user, data.Questions[Closure], TimeSpan.FromDays(-1), Grade.Knew);
        await AddStateAsync(data, user, data.Questions[ConfigureAwait], TimeSpan.FromDays(2), Grade.Knew);
        await AddStateAsync(data, user, data.Questions[ResultDeadlock], TimeSpan.FromDays(-1), Grade.Partial);
        await AddStateAsync(data, user, data.Questions[ThreadSafe], TimeSpan.FromDays(-1), Grade.DidntKnow);
        await AddStateAsync(data, user, data.Questions[Starvation], TimeSpan.FromDays(30), Grade.Knew);
        await using TrainerDbContext db = data.NewContext();

        ProfileDto profile = await CreateQueries(db, data).GetProfileAsync(user, En, Dotnet, Ct);

        Assert.Equal(5, profile.TotalAnswered);
        Assert.Equal(3, profile.DueNow);
        Assert.Equal(
            ["async", "concurrency", "csharp"],
            profile.Topics.Select(t => t.Topic).Order(StringComparer.Ordinal));
        TopicProgressDto asyncTopic = profile.Topics.Single(t => t.Topic == "async");
        Assert.Equal(("async / await", 2, 2, 1, 1), (asyncTopic.Title, asyncTopic.LiveQuestions, asyncTopic.Answered, asyncTopic.Known, asyncTopic.Due));
        TopicProgressDto concurrency = profile.Topics.Single(t => t.Topic == "concurrency");
        Assert.Equal((3, 2, 1, 1), (concurrency.LiveQuestions, concurrency.Answered, concurrency.Known, concurrency.Due));
        TopicProgressDto csharp = profile.Topics.Single(t => t.Topic == "csharp");
        Assert.Equal((1, 1, 1, 1), (csharp.LiveQuestions, csharp.Answered, csharp.Known, csharp.Due));

        Assert.Equal(["junior", "middle", "senior", "lead", "architect"], profile.Levels.Select(l => l.Level));
        Assert.Equal((1, 1, 1), LevelRow(profile, "junior"));
        Assert.Equal((4, 3, 1), LevelRow(profile, "middle"));
        Assert.Equal((1, 1, 1), LevelRow(profile, "senior"));
        Assert.Equal((0, 0, 0), LevelRow(profile, "lead"));
    }

    private static (int Live, int Answered, int Known) LevelRow(ProfileDto profile, string level)
    {
        LevelProgressDto row = profile.Levels.Single(l => l.Level == level);
        return (row.LiveQuestions, row.Answered, row.Known);
    }

    [Fact]
    public async Task Profile_IgnoresRetiredQuestionsAndOtherCultureAndOtherUsers()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        UserId other = await data.CreateUserAsync(OtherTelegramId, Ct);
        await AddQuestionAsync(data, "01KBZ3Q8N9V2K3M4P5R6S7T8X1", russian: false);
        await AddStateAsync(data, user, new QuestionId("01KBZ3Q8N9V2K3M4P5R6S7T8X1"), TimeSpan.FromDays(-1));
        await AddStateAsync(data, user, data.Questions[Starvation], TimeSpan.FromDays(-1));
        await AddStateAsync(data, other, data.Questions[Closure], TimeSpan.FromDays(-1));
        await data.RetireQuestionAsync(data.Questions[Starvation], Ct);
        await using TrainerDbContext db = data.NewContext();
        ProgressQueries queries = CreateQueries(db, data);

        ProfileDto ru = await queries.GetProfileAsync(user, Ru, Dotnet, Ct);
        ProfileDto en = await queries.GetProfileAsync(user, En, Dotnet, Ct);

        Assert.Equal((0, 0), (ru.TotalAnswered, ru.DueNow));
        Assert.Equal((1, 1), (en.TotalAnswered, en.DueNow));
        Assert.Equal(1, en.Topics.Single(t => t.Topic == "async").Answered);
        Assert.Equal(2, ru.Topics.Single(t => t.Topic == "async").LiveQuestions);
        Assert.Equal(3, en.Topics.Single(t => t.Topic == "async").LiveQuestions);
    }

    [Fact]
    public async Task Profile_TopicsSortInTheRequestedCultureNotTheThreadCulture()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        await using TrainerDbContext db = data.NewContext();
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            // /api has no route culture, so the thread culture comes from Accept-Language, not from ?culture=.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en");

            ProfileDto ru = await CreateQueries(db, data).GetProfileAsync(user, Ru, Dotnet, Ct);
            HomeStats home = await new HomeQueries(db).GetStatsAsync(Ru, Dotnet, Ct);
            string[] bootstrap = [.. (await new StackQueries(db).GetTopicTitlesAsync(Ru, Dotnet, Ct)).Select(t => t.Key.Value)];

            // Russian collation puts Cyrillic before Latin: "Многопоточность", "Язык C#", "async / await".
            Assert.Equal(["concurrency", "csharp", "async"], bootstrap);
            Assert.Equal(bootstrap, ru.Topics.Select(t => t.Topic));
            Assert.Equal(bootstrap, home.Topics.Select(t => t.Key));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task Profile_NextDueAt_IsEarliestFuture()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        UserId other = await data.CreateUserAsync(OtherTelegramId, Ct);
        await AddStateAsync(data, user, data.Questions[Closure], TimeSpan.FromDays(-1));
        await AddStateAsync(data, user, data.Questions[ConfigureAwait], TimeSpan.FromDays(5));
        await AddStateAsync(data, user, data.Questions[ResultDeadlock], TimeSpan.FromDays(2));
        await AddStateAsync(data, user, data.Questions[Starvation], TimeSpan.FromHours(1));
        await AddStateAsync(data, other, data.Questions[ThreadSafe], TimeSpan.FromMinutes(1));
        await data.RetireQuestionAsync(data.Questions[Starvation], Ct);
        await using TrainerDbContext db = data.NewContext();
        ProgressQueries queries = CreateQueries(db, data);

        ProfileDto profile = await queries.GetProfileAsync(user, En, Dotnet, Ct);

        Assert.Equal(data.Clock.GetUtcNow().AddDays(2), profile.NextDueAt);
        Assert.Null((await queries.GetProfileAsync(user, Ru, new StackKey("java"), Ct)).NextDueAt);
        Assert.Equal(data.Clock.GetUtcNow().AddMinutes(1), (await queries.GetProfileAsync(other, En, Dotnet, Ct)).NextDueAt);
    }

    [Fact]
    public async Task Profile_RecentMocks_NewestFirstMaxFive()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        UserId other = await data.CreateUserAsync(OtherTelegramId, Ct);
        string[] ids = [.. Enumerable.Range(0, 7).Select(_ => Ids.Next())];
        for (int i = 0; i < ids.Length; i++)
        {
            data.Db.MockSessions.Add(Mock(ids[i], user, data.Clock.GetUtcNow().AddHours(-10 + i), Level.Middle, 10 + i));
        }

        data.Db.MockSessions.Add(Mock(Ids.Next(), other, data.Clock.GetUtcNow().AddMinutes(-1), Level.Senior, 5));
        await data.Db.SaveChangesAsync(Ct);
        await using TrainerDbContext db = data.NewContext();

        ProfileDto profile = await CreateQueries(db, data).GetProfileAsync(user, En, Dotnet, Ct);

        Assert.Equal(5, profile.RecentMocks.Count);
        Assert.Equal([ids[6], ids[5], ids[4], ids[3], ids[2]], profile.RecentMocks.Select(m => m.Id));
        MockHistoryDto newest = profile.RecentMocks[0];
        Assert.Equal("middle", newest.Level);
        Assert.Equal(16, newest.QuestionCount);
        Assert.Equal(data.Clock.GetUtcNow().AddHours(-4), newest.FinishedAt);
        Assert.Equal((5, 3, 2, 1), (newest.Knew, newest.Partial, newest.DidntKnow, newest.Unanswered));
    }

    private static MockSessionEntity Mock(string id, UserId user, DateTimeOffset finishedAt, Level level, int questionCount) => new()
    {
        Id = id,
        UserId = user.Value,
        StackKey = "dotnet",
        Level = level,
        QuestionCount = questionCount,
        Timed = true,
        StartedAt = finishedAt.AddMinutes(-30),
        FinishedAt = finishedAt,
        Summary = """{"topics":[{"topic":"async","knew":3,"partial":1,"didntKnow":1,"unanswered":1},{"topic":"concurrency","knew":2,"partial":2,"didntKnow":1,"unanswered":0}]}""",
    };

    [Fact]
    public async Task Me_MatchesProfileTotals()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        await AddStateAsync(data, user, data.Questions[Closure], TimeSpan.FromDays(-1));
        await AddStateAsync(data, user, data.Questions[ThreadSafe], TimeSpan.FromDays(4), Grade.Partial);
        await AddAnswerAsync(data, user, data.Clock.GetUtcNow().AddHours(-1));
        await AddAnswerAsync(data, user, data.Clock.GetUtcNow().AddDays(-1));
        await using TrainerDbContext db = data.NewContext();
        ProgressQueries queries = CreateQueries(db, data);

        ProfileDto profile = await queries.GetProfileAsync(user, En, Dotnet, Ct);
        MeDto me = await queries.GetMeAsync(user, En, Dotnet, Ct);

        Assert.Equal(TelegramId, me.TelegramUserId);
        Assert.Equal(2, profile.StreakDays);
        Assert.Equal(profile.StreakDays, me.StreakDays);
        Assert.Equal(1, profile.DueNow);
        Assert.Equal(profile.DueNow, me.DueNow);
        Assert.Equal(profile.Topics, me.Topics);
    }

    [Fact]
    public async Task Streak_UsesUserOffset()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        UserId utcUser = await data.CreateUserAsync(OtherTelegramId, Ct);
        DateTimeOffset first = new(2026, 11, 10, 1, 0, 0, TimeSpan.FromHours(3));
        DateTimeOffset second = new(2026, 11, 11, 23, 30, 0, TimeSpan.FromHours(3));
        foreach (UserId who in new[] { user, utcUser })
        {
            await AddAnswerAsync(data, who, first);
            await AddAnswerAsync(data, who, second);
        }

        await SetOffsetAsync(data, user, 180);
        await using TrainerDbContext db = data.NewContext();
        ProgressQueries queries = CreateQueries(db, data);

        ProfileDto local = await queries.GetProfileAsync(user, En, Dotnet, Ct);
        ProfileDto utc = await queries.GetProfileAsync(utcUser, En, Dotnet, Ct);

        Assert.Equal(2, local.StreakDays);
        Assert.Equal(1, utc.StreakDays);
    }

    [Fact]
    public async Task Streak_EveningLearnerBehindUtc_KeepsStreakOnLocalDay()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        // Offset -300 (UTC-5): the clock reads 2026-11-12T10:00Z, local 05:00 on the 12th. Local days are 11 and 10.
        await AddAnswerAsync(data, user, new DateTimeOffset(2026, 11, 11, 22, 0, 0, TimeSpan.FromHours(-5)));
        await AddAnswerAsync(data, user, new DateTimeOffset(2026, 11, 10, 22, 0, 0, TimeSpan.FromHours(-5)));
        await SetOffsetAsync(data, user, -300);
        await using TrainerDbContext db = data.NewContext();

        ProfileDto profile = await CreateQueries(db, data).GetProfileAsync(user, En, Dotnet, Ct);

        Assert.Equal(2, profile.StreakDays);
    }

    [Fact]
    public async Task Streak_NoAnswers_IsZero()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        await using TrainerDbContext db = data.NewContext();

        ProfileDto profile = await CreateQueries(db, data).GetProfileAsync(user, En, Dotnet, Ct);

        Assert.Equal(0, profile.StreakDays);
        Assert.Equal(0, profile.TotalAnswered);
        Assert.Empty(profile.RecentMocks);
    }

    [Fact]
    public async Task Streak_StopsQueryAtGap()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        DateTimeOffset now = data.Clock.GetUtcNow();
        // A streak of 3 days, a gap, then 40 older days with two answers each.
        foreach (int daysAgo in new[] { 0, 1, 2 })
        {
            await AddAnswerAsync(data, user, now.AddDays(-daysAgo));
        }

        for (int daysAgo = 5; daysAgo < 45; daysAgo++)
        {
            await AddAnswerAsync(data, user, now.AddDays(-daysAgo));
            await AddAnswerAsync(data, user, now.AddDays(-daysAgo).AddMinutes(-5));
        }

        ReadCountInterceptor reads = new();
        DbContextOptionsBuilder<TrainerDbContext> options = new();
        TrainerDbContextOptions.Configure(options, data.ConnectionString);
        options.AddInterceptors(reads);
        await using TrainerDbContext db = new(options.Options);

        ProfileDto profile = await CreateQueries(db, data).GetProfileAsync(user, En, Dotnet, Ct);

        Assert.Equal(3, profile.StreakDays);
        int streakRows = Assert.Single(reads.ReadsOf("answers"));
        Assert.True(streakRows <= 4, $"The streak query read {streakRows} rows; at most streak + 1 = 4 are needed.");
    }

    [Fact]
    public async Task Streak_LongerThan400Days()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        string userId = user.Value;
        string questionId = data.Questions[Closure].Value;
        DateTimeOffset now = data.Clock.GetUtcNow();
        await data.Db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO answers (id, client_answer_id, user_id, question_id, grade, used_hint, mode, answered_at, received_at)
            SELECT 'A' || lpad(g::text, 25, '0'), 'B' || lpad(g::text, 25, '0'), {userId}, {questionId}, 2, false, 1,
                   {now} - g * interval '1 day', {now}
            FROM generate_series(0, 449) AS g
            """,
            Ct);
        await using TrainerDbContext db = data.NewContext();

        ProfileDto profile = await CreateQueries(db, data).GetProfileAsync(user, En, Dotnet, Ct);

        Assert.Equal(450, profile.StreakDays);
    }

    /// <summary>Records how many rows each command's reader actually read before it was disposed.</summary>
    private sealed class ReadCountInterceptor : DbCommandInterceptor
    {
        private readonly List<(string Sql, int Reads)> _commands = [];

        public IEnumerable<int> ReadsOf(string table)
        {
            lock (_commands)
            {
                return [.. _commands.Where(c => c.Sql.Contains(table, StringComparison.Ordinal)).Select(c => c.Reads)];
            }
        }

        public override InterceptionResult DataReaderDisposing(
            DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
        {
            lock (_commands)
            {
                _commands.Add((command.CommandText, eventData.ReadCount));
            }

            return result;
        }
    }
}
