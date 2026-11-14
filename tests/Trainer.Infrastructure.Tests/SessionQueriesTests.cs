using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Trainer.Application.Sessions;
using Trainer.Contracts;
using Trainer.Domain.Grading;
using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;
using Trainer.Domain.Users;
using Trainer.Infrastructure.Persistence;
using Trainer.Infrastructure.Persistence.Entities;
using Trainer.Infrastructure.Sessions;
using Trainer.TestSupport;

namespace Trainer.Infrastructure.Tests;

public sealed class SessionQueriesTests(PostgresFixture postgres)
{
    private const long TelegramId = 600_700_800L;
    private const long OtherTelegramId = 600_700_801L;

    private static readonly CultureKey En = new("en");
    private static readonly CultureKey Ru = new("ru");
    private static readonly StackKey Dotnet = new("dotnet");

    // The six imported fixture questions, in id order.
    private const int Closure = 0;
    private const int ConfigureAwait = 1;
    private const int ResultDeadlock = 2;
    private const int ThreadSafe = 3;
    private const int ParallelRace = 4;
    private const int Starvation = 5;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SessionQueries CreateQueries(TrainerDbContext db, LearningDatabase data) =>
        new(db, data.Clock, new MockComposer(new Random(11)));

    private static PracticeCriteria Practice(
        CultureKey? culture = null,
        Level[]? levels = null,
        string[]? topics = null,
        QuestionType[]? types = null,
        PracticeStatus status = PracticeStatus.All,
        int count = 20,
        QuestionId? first = null,
        QuestionId[]? exclude = null) =>
        new(
            culture ?? En,
            Dotnet,
            levels ?? [],
            [.. (topics ?? []).Select(t => new TopicKey(t))],
            types ?? [],
            status,
            count,
            first,
            exclude ?? []);

    private static string[] Ids(SessionDto session) => [.. session.Questions.Select(q => q.Id)];

    private static string[] SortedIds(SessionDto session) => [.. Ids(session).Order(StringComparer.Ordinal)];

    private static string[] SortedIds(LearningDatabase data, params int[] indexes) =>
        [.. indexes.Select(i => data.Questions[i].Value).Order(StringComparer.Ordinal)];

    private static async Task AddStateAsync(LearningDatabase data, UserId user, QuestionId question, TimeSpan dueIn)
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
            LastGrade = Grade.Knew,
            LastAnsweredAt = data.Clock.GetUtcNow().AddDays(-1),
        });
        await data.Db.SaveChangesAsync(Ct);
        data.Db.ChangeTracker.Clear();
    }

    /// <summary>Adds questions to the dotnet stack; <paramref name="topic"/> must be one of the fixture topics.</summary>
    private static async Task<QuestionId> AddQuestionAsync(
        LearningDatabase data,
        string id,
        Level level,
        string topic = "async",
        string stack = "dotnet",
        bool english = true,
        bool russian = true)
    {
        data.Db.Questions.Add(new QuestionEntity
        {
            Id = id,
            StackKey = stack,
            TopicKey = topic,
            Slug = "extra-" + id.ToLowerInvariant(),
            Level = level,
            Type = QuestionType.Open,
            Grading = GradingMode.Self,
            Verified = new DateOnly(2026, 11, 11),
        });
        foreach ((bool present, string culture) in new[] { (english, "en"), (russian, "ru") })
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
        return new QuestionId(id);
    }

    private static async Task AddBulkAsync(LearningDatabase data, string prefix, Level level, int count)
    {
        for (int i = 0; i < count; i++)
        {
            data.Db.Questions.Add(new QuestionEntity
            {
                Id = $"{prefix}{i:D6}".PadRight(26, '0'),
                StackKey = "dotnet",
                TopicKey = "async",
                Slug = $"bulk-{prefix.ToLowerInvariant()}-{i}",
                Level = level,
                Type = QuestionType.Open,
                Grading = GradingMode.Self,
                Verified = new DateOnly(2026, 11, 11),
            });
            data.Db.QuestionLocales.Add(new QuestionLocaleEntity
            {
                QuestionId = $"{prefix}{i:D6}".PadRight(26, '0'),
                Culture = "en",
                Title = "Bulk",
                PromptHtml = "<p>p</p>",
                AnswerHtml = "<p>a</p>",
                ExplanationHtml = "<p>e</p>",
                Options = "[{\"id\":\"a\",\"textHtml\":\"<p>A</p>\",\"explanationHtml\":null}]",
                Links = "[]",
            });
        }

        await data.Db.SaveChangesAsync(Ct);
        data.Db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Practice_FiltersAndCount()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        await using TrainerDbContext db = data.NewContext();
        SessionQueries queries = CreateQueries(db, data);

        SessionDto byLevel = await queries.GetPracticeAsync(Practice(levels: [Level.Middle], count: 3), null, Ct);
        SessionDto byTopicAndLevel = await queries.GetPracticeAsync(Practice(levels: [Level.Middle], topics: ["concurrency"]), null, Ct);
        SessionDto byType = await queries.GetPracticeAsync(Practice(types: [QuestionType.Open, QuestionType.Single]), null, Ct);
        SessionDto everything = await queries.GetPracticeAsync(Practice(), null, Ct);

        Assert.Equal(3, byLevel.Questions.Count);
        Assert.Equal(4, byLevel.PoolSize);
        Assert.All(byLevel.Questions, q => Assert.Equal("middle", q.Level));
        Assert.Equal(SortedIds(data, ThreadSafe, ParallelRace), SortedIds(byTopicAndLevel));
        Assert.Equal(2, byTopicAndLevel.PoolSize);
        Assert.Equal(SortedIds(data, ConfigureAwait, ResultDeadlock), SortedIds(byType));
        Assert.Equal(6, everything.PoolSize);
        Assert.Equal(6, everything.Questions.Count);
    }

    [Fact]
    public async Task Practice_ExcludesRetiredAndOtherCultureOnly()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        await data.RetireQuestionAsync(data.Questions[Starvation], Ct);
        QuestionId englishOnly = await AddQuestionAsync(data, "01KBZ3Q8N9V2K3M4P5R6S7T8X1", Level.Junior, russian: false);
        await using TrainerDbContext db = data.NewContext();
        SessionQueries queries = CreateQueries(db, data);

        SessionDto ru = await queries.GetPracticeAsync(Practice(Ru), null, Ct);
        SessionDto en = await queries.GetPracticeAsync(Practice(En), null, Ct);

        Assert.Equal(SortedIds(data, Closure, ConfigureAwait, ResultDeadlock, ThreadSafe, ParallelRace), SortedIds(ru));
        Assert.Equal(5, ru.PoolSize);
        Assert.Contains(englishOnly.Value, Ids(en));
        Assert.DoesNotContain(data.Questions[Starvation].Value, Ids(en));
        Assert.Equal(6, en.PoolSize);
        Assert.All(ru.Questions, q => Assert.DoesNotContain("Extra", q.Title, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Practice_NewForSignedIn_SkipsAnswered()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        UserId other = await data.CreateUserAsync(OtherTelegramId, Ct);
        await AddStateAsync(data, user, data.Questions[ConfigureAwait], TimeSpan.FromDays(3));
        await AddStateAsync(data, user, data.Questions[ThreadSafe], TimeSpan.FromDays(-3));
        await AddStateAsync(data, other, data.Questions[Closure], TimeSpan.FromDays(1));
        await using TrainerDbContext db = data.NewContext();

        SessionDto session = await CreateQueries(db, data).GetPracticeAsync(Practice(status: PracticeStatus.New), user, Ct);

        Assert.Equal(SortedIds(data, Closure, ResultDeadlock, ParallelRace, Starvation), SortedIds(session));
        Assert.Equal(4, session.PoolSize);
    }

    [Fact]
    public async Task Practice_NewForAnonymous_UsesExclude()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        await using TrainerDbContext db = data.NewContext();
        QuestionId[] exclude = [data.Questions[Closure], data.Questions[Starvation]];

        SessionDto session = await CreateQueries(db, data).GetPracticeAsync(
            Practice(status: PracticeStatus.New, exclude: exclude), null, Ct);

        Assert.Equal(SortedIds(data, ConfigureAwait, ResultDeadlock, ThreadSafe, ParallelRace), SortedIds(session));
        Assert.Equal(4, session.PoolSize);
    }

    // "New session" leaves out the questions just seen whatever the status, so Exclude narrows every status; the first
    // question is the one exception (it was asked for by id).
    [Fact]
    public async Task Practice_Exclude_AppliesInEveryStatus()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        await AddStateAsync(data, user, data.Questions[ConfigureAwait], TimeSpan.FromHours(-1));
        await AddStateAsync(data, user, data.Questions[ResultDeadlock], TimeSpan.FromHours(-1));
        await using TrainerDbContext db = data.NewContext();
        SessionQueries queries = CreateQueries(db, data);
        QuestionId[] exclude = [data.Questions[Closure], data.Questions[ConfigureAwait]];

        SessionDto allAnonymous = await queries.GetPracticeAsync(Practice(exclude: exclude), null, Ct);
        SessionDto allSignedIn = await queries.GetPracticeAsync(Practice(exclude: exclude), user, Ct);
        SessionDto newSignedIn = await queries.GetPracticeAsync(Practice(status: PracticeStatus.New, exclude: exclude), user, Ct);
        SessionDto due = await queries.GetPracticeAsync(Practice(status: PracticeStatus.Due, exclude: exclude), user, Ct);
        SessionDto first = await queries.GetPracticeAsync(Practice(exclude: exclude, first: data.Questions[Closure]), null, Ct);

        Assert.Equal(SortedIds(data, ResultDeadlock, ThreadSafe, ParallelRace, Starvation), SortedIds(allAnonymous));
        Assert.Equal(4, allAnonymous.PoolSize);
        Assert.Equal(SortedIds(data, ResultDeadlock, ThreadSafe, ParallelRace, Starvation), SortedIds(allSignedIn));
        Assert.Equal(SortedIds(data, ThreadSafe, ParallelRace, Starvation), SortedIds(newSignedIn));
        Assert.Equal(SortedIds(data, ResultDeadlock), SortedIds(due));
        Assert.Equal(data.Questions[Closure].Value, first.Questions[0].Id);
        Assert.Equal(5, first.PoolSize);
    }

    [Fact]
    public async Task Practice_Due_ReturnsOnlyDue()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        await AddStateAsync(data, user, data.Questions[ConfigureAwait], TimeSpan.FromHours(-1));
        await AddStateAsync(data, user, data.Questions[ResultDeadlock], TimeSpan.Zero);
        await AddStateAsync(data, user, data.Questions[ThreadSafe], TimeSpan.FromMinutes(1));
        await using TrainerDbContext db = data.NewContext();
        SessionQueries queries = CreateQueries(db, data);

        SessionDto signedIn = await queries.GetPracticeAsync(Practice(status: PracticeStatus.Due), user, Ct);
        SessionDto anonymous = await queries.GetPracticeAsync(Practice(status: PracticeStatus.Due), null, Ct);

        Assert.Equal(SortedIds(data, ConfigureAwait, ResultDeadlock), SortedIds(signedIn));
        Assert.Equal(2, signedIn.PoolSize);
        Assert.Empty(anonymous.Questions);
        Assert.Equal(0, anonymous.PoolSize);
    }

    [Fact]
    public async Task Practice_AnonymousDueWithFirst_ReturnsOnlyTheFirstQuestion()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        await using TrainerDbContext db = data.NewContext();
        SessionQueries queries = CreateQueries(db, data);

        // First is outside every filter, Due included (the API refuses anonymous due, so this only documents the query).
        SessionDto session = await queries.GetPracticeAsync(
            Practice(status: PracticeStatus.Due, first: data.Questions[Closure]), null, Ct);

        QuestionDto only = Assert.Single(session.Questions);
        Assert.Equal(data.Questions[Closure].Value, only.Id);
        Assert.Equal(1, session.PoolSize);
    }

    [Fact]
    public async Task Practice_FirstQuestionComesFirstOnceAndCounts()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        await using TrainerDbContext db = data.NewContext();
        SessionQueries queries = CreateQueries(db, data);

        for (int round = 0; round < 8; round++)
        {
            SessionDto inPool = await queries.GetPracticeAsync(
                Practice(levels: [Level.Middle], count: 3, first: data.Questions[ThreadSafe]), null, Ct);
            SessionDto outsideFilters = await queries.GetPracticeAsync(
                Practice(levels: [Level.Middle], count: 3, first: data.Questions[Closure]), null, Ct);

            Assert.Equal(data.Questions[ThreadSafe].Value, inPool.Questions[0].Id);
            Assert.Equal(3, inPool.Questions.Count);
            Assert.Equal(3, Ids(inPool).Distinct().Count());
            Assert.Equal(4, inPool.PoolSize);
            Assert.Equal(data.Questions[Closure].Value, outsideFilters.Questions[0].Id);
            Assert.Equal(3, outsideFilters.Questions.Count);
            Assert.Equal(3, Ids(outsideFilters).Distinct().Count());
            Assert.Equal(5, outsideFilters.PoolSize);
        }
    }

    [Fact]
    public async Task Practice_FirstFromOtherStack_Ignored()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        data.Db.Stacks.Add(new StackEntity { Key = "java" });
        data.Db.Topics.Add(new TopicEntity { StackKey = "java", Key = "jvm" });
        await data.Db.SaveChangesAsync(Ct);
        QuestionId foreign = await AddQuestionAsync(data, "01KBZ3Q8N9V2K3M4P5R6S7T8X2", Level.Junior, topic: "jvm", stack: "java");
        QuestionId retired = await AddQuestionAsync(data, "01KBZ3Q8N9V2K3M4P5R6S7T8X3", Level.Junior);
        await data.RetireQuestionAsync(retired, Ct);
        await using TrainerDbContext db = data.NewContext();
        SessionQueries queries = CreateQueries(db, data);

        SessionDto foreignFirst = await queries.GetPracticeAsync(Practice(count: 6, first: foreign), null, Ct);
        SessionDto retiredFirst = await queries.GetPracticeAsync(Practice(count: 6, first: retired), null, Ct);

        Assert.DoesNotContain(foreign.Value, Ids(foreignFirst));
        Assert.Equal(6, foreignFirst.PoolSize);
        Assert.DoesNotContain(retired.Value, Ids(retiredFirst));
        Assert.Equal(6, retiredFirst.PoolSize);
    }

    [Fact]
    public async Task Practice_NoMatches_ReturnsEmptyWithPoolZero()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        await using TrainerDbContext db = data.NewContext();

        SessionDto session = await CreateQueries(db, data).GetPracticeAsync(Practice(levels: [Level.Architect]), null, Ct);

        Assert.Empty(session.Questions);
        Assert.Equal(0, session.PoolSize);
    }

    [Fact]
    public async Task Practice_QuestionDto_CarriesCorrectnessAndCodeLines()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        await using TrainerDbContext db = data.NewContext();

        SessionDto session = await CreateQueries(db, data).GetPracticeAsync(Practice(), null, Ct);

        QuestionDto closure = session.Questions.Single(q => q.Id == data.Questions[Closure].Value);
        Assert.Equal("closure-for-loop", closure.Slug);
        Assert.Equal("csharp", closure.Topic);
        Assert.Equal("junior", closure.Level);
        Assert.Equal("output", closure.Type);
        Assert.False(string.IsNullOrWhiteSpace(closure.TopicTitle));
        Assert.NotNull(closure.CodeLinesHtml);
        Assert.NotEmpty(closure.CodeLinesHtml);
        Assert.StartsWith("<span class=\"line\" data-line=\"1\">", closure.CodeLinesHtml[0], StringComparison.Ordinal);
        Assert.NotNull(closure.AcceptedOutputs);
        Assert.NotEmpty(closure.AcceptedOutputs);
        Assert.NotEmpty(closure.Links);

        QuestionDto multi = session.Questions.Single(q => q.Id == data.Questions[ThreadSafe].Value);
        Assert.Equal("multi", multi.Type);
        Assert.Contains(multi.Options, o => o.Correct);
        Assert.Contains(multi.Options, o => !o.Correct);
        Assert.All(multi.Options, o => Assert.False(string.IsNullOrWhiteSpace(o.TextHtml)));
        Assert.Null(multi.CodeLinesHtml);

        QuestionDto bug = session.Questions.Single(q => q.Id == data.Questions[ParallelRace].Value);
        Assert.Equal("find-bug", bug.Type);
        Assert.NotNull(bug.BugLineSets);
        Assert.NotEmpty(bug.BugLineSets);
        Assert.NotNull(bug.CodeLinesHtml);
        Assert.False(string.IsNullOrWhiteSpace(bug.PromptHtml));
        Assert.False(string.IsNullOrWhiteSpace(bug.AnswerHtml));
        Assert.False(string.IsNullOrWhiteSpace(bug.ExplanationHtml));
    }

    [Fact]
    public async Task Mock_ReturnsComposedSetAndWarnsOnSmallPool()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        await using TrainerDbContext db = data.NewContext();
        SessionQueries queries = CreateQueries(db, data);
        MockCriteria criteria = new(En, Dotnet, Level.Middle, [], 20);

        // Four middle questions plus one junior question.
        MockSessionDto small = await queries.GetMockAsync(criteria, Ct);

        Assert.Equal(5, small.PoolSize);
        Assert.Equal(5, small.Questions.Count);
        Assert.True(small.RepeatsLikely);
        Assert.Equal(5, small.SuggestedCount);

        await AddBulkAsync(data, "BM", Level.Middle, 30);
        await AddBulkAsync(data, "BJ", Level.Junior, 30);
        MockSessionDto large = await queries.GetMockAsync(criteria, Ct);

        Assert.Equal(65, large.PoolSize);
        Assert.Equal(20, large.Questions.Count);
        Assert.Equal(20, large.Questions.Select(q => q.Id).Distinct().Count());
        Assert.Equal(6, large.Questions.Count(q => q.Level == "junior"));
        Assert.Equal(14, large.Questions.Count(q => q.Level == "middle"));
        Assert.False(large.RepeatsLikely);
        Assert.Equal(21, large.SuggestedCount);
    }

    [Fact]
    public async Task Mock_Junior_UsesOnlyTheJuniorPool()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        await using TrainerDbContext db = data.NewContext();
        int juniorCount = await data.Db.Questions.CountAsync(q => q.Level == Level.Junior && q.RetiredAt == null, Ct);

        MockSessionDto session = await CreateQueries(db, data).GetMockAsync(new MockCriteria(En, Dotnet, Level.Junior, [], 20), Ct);

        Assert.True(juniorCount > 0);
        Assert.Equal(juniorCount, session.PoolSize);
        Assert.Equal(juniorCount, session.Questions.Count);
        Assert.All(session.Questions, q => Assert.Equal("junior", q.Level));
    }

    [Fact]
    public async Task Mock_PoolUnderFive_ReturnsWholePool()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        await using TrainerDbContext db = data.NewContext();

        // Topic csharp has only the junior closure question, which is the level below Middle.
        MockSessionDto session = await CreateQueries(db, data).GetMockAsync(
            new MockCriteria(En, Dotnet, Level.Middle, [new TopicKey("csharp")], 20), Ct);

        QuestionDto only = Assert.Single(session.Questions);
        Assert.Equal(data.Questions[Closure].Value, only.Id);
        Assert.Equal(1, session.PoolSize);
        Assert.Equal(1, session.SuggestedCount);
        Assert.True(session.RepeatsLikely);
    }

    [Fact]
    public async Task Review_DueOnly_MostOverdueFirst_DropsRetired()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        UserId other = await data.CreateUserAsync(OtherTelegramId, Ct);
        await AddStateAsync(data, user, data.Questions[ConfigureAwait], TimeSpan.FromDays(-3));
        await AddStateAsync(data, user, data.Questions[ResultDeadlock], TimeSpan.FromDays(-1));
        await AddStateAsync(data, user, data.Questions[ThreadSafe], TimeSpan.FromDays(-5));
        await AddStateAsync(data, user, data.Questions[ParallelRace], TimeSpan.FromDays(2));
        await AddStateAsync(data, user, data.Questions[Starvation], TimeSpan.FromDays(-9));
        await AddStateAsync(data, other, data.Questions[Closure], TimeSpan.FromDays(-30));
        await data.RetireQuestionAsync(data.Questions[Starvation], Ct);
        await using TrainerDbContext db = data.NewContext();

        SessionDto session = await CreateQueries(db, data).GetReviewAsync(new ReviewCriteria(En, Dotnet, 20), user, Ct);
        SessionDto limited = await CreateQueries(db, data).GetReviewAsync(new ReviewCriteria(En, Dotnet, 2), user, Ct);

        Assert.Equal(
            [data.Questions[ThreadSafe].Value, data.Questions[ConfigureAwait].Value, data.Questions[ResultDeadlock].Value],
            Ids(session));
        Assert.Equal(3, session.PoolSize);
        Assert.Equal([data.Questions[ThreadSafe].Value, data.Questions[ConfigureAwait].Value], Ids(limited));
        Assert.Equal(3, limited.PoolSize);
    }

    [Fact]
    public async Task Review_DropsQuestionsWithoutTheCultureLocale()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        QuestionId englishOnly = await AddQuestionAsync(data, "01KBZ3Q8N9V2K3M4P5R6S7T8X4", Level.Middle, russian: false);
        await AddStateAsync(data, user, englishOnly, TimeSpan.FromDays(-1));
        await AddStateAsync(data, user, data.Questions[ConfigureAwait], TimeSpan.FromDays(-1));
        await using TrainerDbContext db = data.NewContext();

        SessionDto ru = await CreateQueries(db, data).GetReviewAsync(new ReviewCriteria(Ru, Dotnet, 20), user, Ct);

        Assert.Equal([data.Questions[ConfigureAwait].Value], Ids(ru));
    }

    [Fact]
    public async Task Practice_SessionOfFifty_UsesConstantQueryCount()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        await AddBulkAsync(data, "BP", Level.Middle, 60);
        CommandCounter counter = new();
        DbContextOptionsBuilder<TrainerDbContext> builder = new();
        TrainerDbContextOptions.Configure(builder, data.ConnectionString);
        builder.AddInterceptors(counter);
        await using TrainerDbContext db = new(builder.Options);
        SessionQueries queries = CreateQueries(db, data);

        SessionDto one = await queries.GetPracticeAsync(Practice(status: PracticeStatus.New, count: 1), user, Ct);
        int commandsForOne = counter.Commands;
        counter.Reset();
        SessionDto fifty = await queries.GetPracticeAsync(Practice(status: PracticeStatus.New, count: 50, first: data.Questions[Closure]), user, Ct);
        int commandsForFifty = counter.Commands;

        Assert.Single(one.Questions);
        Assert.Equal(50, fifty.Questions.Count);
        Assert.InRange(commandsForOne, 1, 3);
        Assert.InRange(commandsForFifty, 1, 3);
    }

    [Fact]
    public async Task Queries_RejectCountsOutsideTheLimits()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        await using TrainerDbContext db = data.NewContext();
        SessionQueries queries = CreateQueries(db, data);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => queries.GetPracticeAsync(Practice(count: 0), null, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => queries.GetPracticeAsync(Practice(count: 51), null, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => queries.GetMockAsync(new MockCriteria(En, Dotnet, Level.Middle, [], 4), Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => queries.GetMockAsync(new MockCriteria(En, Dotnet, Level.Middle, [], 51), Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => queries.GetReviewAsync(new ReviewCriteria(En, Dotnet, 0), user, Ct));
        await Assert.ThrowsAsync<ArgumentException>(
            () => queries.GetPracticeAsync(Practice(exclude: [.. Enumerable.Repeat(data.Questions[0], SessionLimits.MaxExcludedQuestions + 1)]), null, Ct));
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Commands { get; private set; }

        public void Reset() => Commands = 0;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands++;
            return new ValueTask<InterceptionResult<DbDataReader>>(result);
        }
    }
}
