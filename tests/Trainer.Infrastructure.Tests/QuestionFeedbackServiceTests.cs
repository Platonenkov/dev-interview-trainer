using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Trainer.Application.Feedback;
using Trainer.Domain.Feedback;
using Trainer.Domain.Ids;
using Trainer.Domain.Keys;
using Trainer.Domain.Questions;
using Trainer.Domain.Users;
using Trainer.Infrastructure.Feedback;
using Trainer.Infrastructure.Persistence;
using Trainer.Infrastructure.Persistence.Entities;
using Trainer.TestSupport;

namespace Trainer.Infrastructure.Tests;

public sealed class QuestionFeedbackServiceTests(PostgresFixture postgres)
{
    private const long FirstTelegramId = 710_000_001L;
    private const long SecondTelegramId = 710_000_002L;
    private const long ThirdTelegramId = 710_000_003L;

    private static readonly CultureKey En = new("en");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static QuestionFeedbackService CreateService(LearningDatabase data, TrainerDbContext db, int reportsPerDay = 10) =>
        new(db, Options.Create(new FeedbackOptions { ReportsPerDay = reportsPerDay }), new UlidGenerator(data.Clock), data.Clock);

    private static QuestionFeedbackQueries CreateQueries(TrainerDbContext db) => new(db);

    /// <summary>The i-th of 30 distinct (question, reason) pairs, so a test can file many reports that never merge.</summary>
    private static (int Question, ReportReason Reason) Combination(int index) => (index % 6, ReportReasonKeys.All[index / 6]);

    private static async Task<ReportResult> ReportCombinationAsync(
        LearningDatabase data, UserId user, int index, int reportsPerDay = 10, string? comment = null)
    {
        (int question, ReportReason reason) = Combination(index);
        await using TrainerDbContext db = data.NewContext();
        return await CreateService(data, db, reportsPerDay).ReportAsync(user, data.Questions[question], En, reason, comment, Ct);
    }

    private static Task<List<QuestionRatingEntity>> RatingsAsync(LearningDatabase data) =>
        data.Db.QuestionRatings.AsNoTracking().OrderBy(r => r.UserId).ThenBy(r => r.QuestionId).ToListAsync(Ct);

    private static Task<List<QuestionReportEntity>> ReportsAsync(LearningDatabase data) =>
        data.Db.QuestionReports.AsNoTracking().OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(Ct);

    [Fact]
    public async Task SetRating_ChangeAndClear_KeepsOneRow()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(FirstTelegramId, Ct);
        QuestionId question = data.Questions[0];
        await using TrainerDbContext db = data.NewContext();
        QuestionFeedbackService service = CreateService(data, db);

        RatingResult liked = await service.SetRatingAsync(user, question, QuestionRating.Like, Ct);
        Assert.Equal(new RatingResult(FeedbackOutcome.Saved, QuestionRating.Like), liked);
        Assert.Equal([QuestionRating.Like], (await RatingsAsync(data)).Select(r => r.Value));

        data.Clock.Advance(TimeSpan.FromMinutes(1));
        RatingResult disliked = await service.SetRatingAsync(user, question, QuestionRating.Dislike, Ct);
        Assert.Equal(new RatingResult(FeedbackOutcome.Saved, QuestionRating.Dislike), disliked);
        List<QuestionRatingEntity> rows = await RatingsAsync(data);
        QuestionRatingEntity row = Assert.Single(rows);
        Assert.Equal(QuestionRating.Dislike, row.Value);
        Assert.Equal(data.Clock.GetUtcNow(), row.UpdatedAt);

        RatingResult cleared = await service.SetRatingAsync(user, question, null, Ct);
        Assert.Equal(new RatingResult(FeedbackOutcome.Saved, null), cleared);
        Assert.Empty(await RatingsAsync(data));

        RatingResult clearedAgain = await service.SetRatingAsync(user, question, null, Ct);
        Assert.Equal(FeedbackOutcome.Saved, clearedAgain.Outcome);
    }

    [Fact]
    public async Task SetRating_ConcurrentSameRating_StoresOneRow()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(FirstTelegramId, Ct);
        QuestionId question = data.Questions[0];

        RatingResult[] results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using TrainerDbContext db = data.NewContext();
            return await CreateService(data, db).SetRatingAsync(user, question, QuestionRating.Like, Ct);
        }));

        Assert.All(results, r => Assert.Equal(FeedbackOutcome.Saved, r.Outcome));
        Assert.Single(await RatingsAsync(data));
    }

    [Fact]
    public async Task SetRating_RetiredQuestion_IsNotFound()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(FirstTelegramId, Ct);
        await data.RetireQuestionAsync(data.Questions[0], Ct);
        await using TrainerDbContext db = data.NewContext();
        QuestionFeedbackService service = CreateService(data, db);

        RatingResult retired = await service.SetRatingAsync(user, data.Questions[0], QuestionRating.Like, Ct);
        RatingResult unknown = await service.SetRatingAsync(user, new QuestionId("01JD0000000000000000000000"), QuestionRating.Like, Ct);

        Assert.Equal(new RatingResult(FeedbackOutcome.QuestionNotFound, null), retired);
        Assert.Equal(new RatingResult(FeedbackOutcome.QuestionNotFound, null), unknown);
        Assert.Empty(await RatingsAsync(data));
    }

    [Fact]
    public async Task SetRating_QuestionOfRetiredTopic_IsNotFound()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(FirstTelegramId, Ct);
        QuestionEntity question = await data.Db.Questions.AsNoTracking().SingleAsync(q => q.Id == data.Questions[0].Value, Ct);
        DateTimeOffset now = data.Clock.GetUtcNow();
        await data.Db.Topics
            .Where(t => t.StackKey == question.StackKey && t.Key == question.TopicKey)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RetiredAt, now), Ct);
        await using TrainerDbContext db = data.NewContext();

        RatingResult result = await CreateService(data, db).SetRatingAsync(user, data.Questions[0], QuestionRating.Like, Ct);

        Assert.Equal(FeedbackOutcome.QuestionNotFound, result.Outcome);
    }

    [Fact]
    public async Task Report_NewReport_IsStoredWithItsFields()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(FirstTelegramId, Ct);
        await using TrainerDbContext db = data.NewContext();

        ReportResult result = await CreateService(data, db)
            .ReportAsync(user, data.Questions[1], new CultureKey("ru"), ReportReason.Outdated, "line one\nline two", Ct);

        Assert.Equal(new ReportResult(ReportOutcome.Created, null), result);
        QuestionReportEntity row = Assert.Single(await ReportsAsync(data));
        Assert.True(UlidText.IsValid(row.Id));
        Assert.Equal(user.Value, row.UserId);
        Assert.Equal(data.Questions[1].Value, row.QuestionId);
        Assert.Equal("ru", row.Culture);
        Assert.Equal(ReportReason.Outdated, row.Reason);
        Assert.Equal("line one\nline two", row.Comment);
        Assert.Equal(data.Clock.GetUtcNow(), row.CreatedAt);
        Assert.Null(row.ResolvedAt);
        Assert.Null(row.ResolvedBy);
    }

    [Fact]
    public async Task Report_RetiredQuestion_IsNotFound()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(FirstTelegramId, Ct);
        await data.RetireQuestionAsync(data.Questions[0], Ct);
        await using TrainerDbContext db = data.NewContext();

        ReportResult result = await CreateService(data, db).ReportAsync(user, data.Questions[0], En, ReportReason.Other, null, Ct);

        Assert.Equal(new ReportResult(ReportOutcome.QuestionNotFound, null), result);
        Assert.Empty(await ReportsAsync(data));
    }

    [Fact]
    public async Task Report_SameReasonWhileOpen_UpdatesInsteadOfCreating()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(FirstTelegramId, Ct);
        await using TrainerDbContext db = data.NewContext();
        QuestionFeedbackService service = CreateService(data, db);
        await service.ReportAsync(user, data.Questions[0], En, ReportReason.WrongAnswer, "first", Ct);
        DateTimeOffset createdAt = data.Clock.GetUtcNow();
        data.Clock.Advance(TimeSpan.FromHours(1));

        ReportResult updated = await service.ReportAsync(user, data.Questions[0], new CultureKey("ru"), ReportReason.WrongAnswer, null, Ct);
        ReportResult otherReason = await service.ReportAsync(user, data.Questions[0], En, ReportReason.Outdated, "other", Ct);

        Assert.Equal(new ReportResult(ReportOutcome.Updated, null), updated);
        Assert.Equal(ReportOutcome.Created, otherReason.Outcome);
        List<QuestionReportEntity> rows = await ReportsAsync(data);
        Assert.Equal(2, rows.Count);
        QuestionReportEntity edited = rows.Single(r => r.Reason == ReportReason.WrongAnswer);
        Assert.Null(edited.Comment);
        Assert.Equal("ru", edited.Culture);
        Assert.Equal(createdAt, edited.CreatedAt);
    }

    [Fact]
    public async Task Report_UpdateOfOpenReport_DoesNotCountAgainstTheQuota()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(FirstTelegramId, Ct);
        await using TrainerDbContext db = data.NewContext();
        QuestionFeedbackService service = CreateService(data, db, reportsPerDay: 2);
        await service.ReportAsync(user, data.Questions[0], En, ReportReason.WrongAnswer, "a", Ct);
        await service.ReportAsync(user, data.Questions[1], En, ReportReason.WrongAnswer, "b", Ct);

        ReportResult updated = await service.ReportAsync(user, data.Questions[0], En, ReportReason.WrongAnswer, "a, edited", Ct);
        ReportResult third = await service.ReportAsync(user, data.Questions[2], En, ReportReason.WrongAnswer, "c", Ct);

        Assert.Equal(ReportOutcome.Updated, updated.Outcome);
        Assert.Equal(ReportOutcome.LimitReached, third.Outcome);
    }

    [Fact]
    public async Task Report_EleventhWithin24h_LimitReachedWithRetryAt()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(FirstTelegramId, Ct);
        DateTimeOffset first = data.Clock.GetUtcNow();
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(ReportOutcome.Created, (await ReportCombinationAsync(data, user, i)).Outcome);
            data.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        ReportResult limited = await ReportCombinationAsync(data, user, 10);

        Assert.Equal(new ReportResult(ReportOutcome.LimitReached, first + TimeSpan.FromHours(24)), limited);
        Assert.Equal(10, (await ReportsAsync(data)).Count);

        // Once the oldest report leaves the rolling window, one more is allowed, and the next is limited again.
        data.Clock.SetUtcNow(first + TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));
        Assert.Equal(ReportOutcome.Created, (await ReportCombinationAsync(data, user, 10)).Outcome);
        ReportResult again = await ReportCombinationAsync(data, user, 11);
        Assert.Equal(ReportOutcome.LimitReached, again.Outcome);
        Assert.Equal(first + TimeSpan.FromMinutes(1) + TimeSpan.FromHours(24), again.RetryAt);
    }

    [Fact]
    public async Task Report_QuotaIsPerUser()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId first = await data.CreateUserAsync(FirstTelegramId, Ct);
        UserId second = await data.CreateUserAsync(SecondTelegramId, Ct);
        for (int i = 0; i < 3; i++)
        {
            await ReportCombinationAsync(data, first, i, reportsPerDay: 3);
        }

        Assert.Equal(ReportOutcome.LimitReached, (await ReportCombinationAsync(data, first, 3, reportsPerDay: 3)).Outcome);
        Assert.Equal(ReportOutcome.Created, (await ReportCombinationAsync(data, second, 3, reportsPerDay: 3)).Outcome);
    }

    [Fact]
    public async Task Report_ConcurrentAtLimit_NeverExceedsQuota()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(FirstTelegramId, Ct);
        for (int i = 0; i < 9; i++)
        {
            await ReportCombinationAsync(data, user, i);
        }

        ReportResult[] results = await Task.WhenAll(Enumerable.Range(9, 5).Select(i => ReportCombinationAsync(data, user, i)));

        Assert.Equal(1, results.Count(r => r.Outcome == ReportOutcome.Created));
        Assert.Equal(4, results.Count(r => r.Outcome == ReportOutcome.LimitReached));
        Assert.Equal(10, (await ReportsAsync(data)).Count);
    }

    [Fact]
    public async Task Report_ConcurrentSameReason_StoresOneOpenReport()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(FirstTelegramId, Ct);

        ReportResult[] results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => ReportCombinationAsync(data, user, 0, comment: "c" + i)));

        Assert.Equal(1, results.Count(r => r.Outcome == ReportOutcome.Created));
        Assert.Equal(3, results.Count(r => r.Outcome == ReportOutcome.Updated));
        Assert.Single(await ReportsAsync(data));
    }

    [Fact]
    public async Task Report_AfterResolve_SameReasonCreatesNew()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(FirstTelegramId, Ct);
        UserId admin = await data.CreateUserAsync(SecondTelegramId, Ct);
        await using TrainerDbContext db = data.NewContext();
        QuestionFeedbackService service = CreateService(data, db);
        await service.ReportAsync(user, data.Questions[0], En, ReportReason.WrongAnswer, "first", Ct);
        ReportId id = new((await ReportsAsync(data)).Single().Id);

        Assert.True(await service.ResolveReportAsync(id, admin, Ct));
        ReportResult second = await service.ReportAsync(user, data.Questions[0], En, ReportReason.WrongAnswer, "again", Ct);

        Assert.Equal(ReportOutcome.Created, second.Outcome);
        List<QuestionReportEntity> rows = await ReportsAsync(data);
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, r => r.ResolvedAt is null);
    }

    [Fact]
    public async Task ResolveReport_StoresWhoAndWhen_AndIsOneShot()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(FirstTelegramId, Ct);
        UserId admin = await data.CreateUserAsync(SecondTelegramId, Ct);
        UserId otherAdmin = await data.CreateUserAsync(ThirdTelegramId, Ct);
        await using TrainerDbContext db = data.NewContext();
        QuestionFeedbackService service = CreateService(data, db);
        await service.ReportAsync(user, data.Questions[0], En, ReportReason.Other, null, Ct);
        ReportId id = new((await ReportsAsync(data)).Single().Id);
        data.Clock.Advance(TimeSpan.FromHours(2));
        DateTimeOffset resolvedAt = data.Clock.GetUtcNow();

        Assert.True(await service.ResolveReportAsync(id, admin, Ct));
        data.Clock.Advance(TimeSpan.FromHours(1));
        Assert.False(await service.ResolveReportAsync(id, otherAdmin, Ct));
        Assert.False(await service.ResolveReportAsync(new ReportId("01JD0000000000000000000000"), admin, Ct));

        QuestionReportEntity row = Assert.Single(await ReportsAsync(data));
        Assert.Equal(resolvedAt, row.ResolvedAt);
        Assert.Equal(admin.Value, row.ResolvedBy);
    }

    [Fact]
    public async Task SetRating_ClearOnQuestionRetiredAfterRating_RemovesTheRow()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(FirstTelegramId, Ct);
        await using TrainerDbContext db = data.NewContext();
        QuestionFeedbackService service = CreateService(data, db);
        await service.SetRatingAsync(user, data.Questions[0], QuestionRating.Like, Ct);
        await data.RetireQuestionAsync(data.Questions[0], Ct);

        RatingResult cleared = await service.SetRatingAsync(user, data.Questions[0], null, Ct);

        Assert.Equal(new RatingResult(FeedbackOutcome.Saved, null), cleared);
        Assert.Empty(await RatingsAsync(data));
    }

    [Fact]
    public async Task Report_TooLongComment_ThrowsBeforeStoringAnything()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(FirstTelegramId, Ct);
        await using TrainerDbContext db = data.NewContext();

        await Assert.ThrowsAsync<ArgumentException>(() => CreateService(data, db)
            .ReportAsync(user, data.Questions[0], En, ReportReason.Other, new string('x', ReportComment.MaxLength + 1), Ct));

        Assert.Empty(await ReportsAsync(data));
    }

    [Fact]
    public async Task Report_CommentWithNul_IsStoredWithoutIt()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(FirstTelegramId, Ct);
        await using TrainerDbContext db = data.NewContext();

        ReportResult result = await CreateService(data, db).ReportAsync(user, data.Questions[0], En, ReportReason.Other, "a\0b\r\nc", Ct);

        Assert.Equal(ReportOutcome.Created, result.Outcome);
        Assert.Equal("ab\nc", Assert.Single(await ReportsAsync(data)).Comment);
    }

    [Fact]
    public async Task Report_WhitespaceOnlyComment_IsStoredAsNull()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(FirstTelegramId, Ct);
        await using TrainerDbContext db = data.NewContext();
        QuestionFeedbackService service = CreateService(data, db);

        await service.ReportAsync(user, data.Questions[0], En, ReportReason.Other, "  \n\t ", Ct);
        await service.ReportAsync(user, data.Questions[0], En, ReportReason.Other, "   ", Ct);

        Assert.Null(Assert.Single(await ReportsAsync(data)).Comment);
    }

    [Fact]
    public async Task MostDisliked_ExcludesRetiredQuestionsBeforeTakingTheTop()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId first = await data.CreateUserAsync(FirstTelegramId, Ct);
        UserId second = await data.CreateUserAsync(SecondTelegramId, Ct);
        await using TrainerDbContext db = data.NewContext();
        QuestionFeedbackService service = CreateService(data, db);
        await service.SetRatingAsync(first, data.Questions[0], QuestionRating.Dislike, Ct);
        await service.SetRatingAsync(second, data.Questions[0], QuestionRating.Dislike, Ct);
        await service.SetRatingAsync(first, data.Questions[1], QuestionRating.Dislike, Ct);
        foreach (QuestionId other in data.Questions.Skip(2))
        {
            await service.SetRatingAsync(first, other, QuestionRating.Dislike, Ct);
        }

        await data.RetireQuestionAsync(data.Questions[0], Ct);
        QuestionEntity inRetiredTopic = await data.Db.Questions.AsNoTracking().SingleAsync(q => q.Id == data.Questions[1].Value, Ct);
        DateTimeOffset now = data.Clock.GetUtcNow();
        await data.Db.Topics
            .Where(t => t.StackKey == inRetiredTopic.StackKey && t.Key == inRetiredTopic.TopicKey)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RetiredAt, now), Ct);
        QuestionFeedbackQueries queries = CreateQueries(data.NewContext());

        IReadOnlyList<RatingTotals> top = await queries.GetMostDislikedAsync(1, CultureKey.Default, Ct);
        IReadOnlyList<RatingTotals> all = await queries.GetMostDislikedAsync(10, CultureKey.Default, Ct);

        Assert.DoesNotContain(all, t => t.QuestionId == data.Questions[0]);
        List<QuestionEntity> stored = await data.Db.Questions.AsNoTracking().ToListAsync(Ct);
        string[] expected = [.. stored
            .Where(q => q.Id != data.Questions[0].Value && !(q.StackKey == inRetiredTopic.StackKey && q.TopicKey == inRetiredTopic.TopicKey))
            .Select(q => q.Id)
            .Order(StringComparer.Ordinal)];
        Assert.NotEmpty(expected);
        Assert.Equal(expected, all.Select(t => t.QuestionId.Value).Order(StringComparer.Ordinal));
        // Every survivor has one dislike, so the tie breaks on the smallest id.
        Assert.Equal(expected[0], Assert.Single(top).QuestionId.Value);
    }

    [Fact]
    public async Task GetMyRatings_ReturnsOnlyRequestedIdsOfThatUser()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId first = await data.CreateUserAsync(FirstTelegramId, Ct);
        UserId second = await data.CreateUserAsync(SecondTelegramId, Ct);
        await using TrainerDbContext db = data.NewContext();
        QuestionFeedbackService service = CreateService(data, db);
        await service.SetRatingAsync(first, data.Questions[0], QuestionRating.Like, Ct);
        await service.SetRatingAsync(first, data.Questions[1], QuestionRating.Dislike, Ct);
        await service.SetRatingAsync(first, data.Questions[2], QuestionRating.Like, Ct);
        await service.SetRatingAsync(second, data.Questions[0], QuestionRating.Dislike, Ct);
        QuestionFeedbackQueries queries = CreateQueries(data.NewContext());

        IReadOnlyDictionary<QuestionId, QuestionRating> mine =
            await queries.GetMyRatingsAsync(first, [data.Questions[0], data.Questions[1], data.Questions[3]], Ct);
        IReadOnlyDictionary<QuestionId, QuestionRating> none = await queries.GetMyRatingsAsync(first, [], Ct);

        Assert.Equal(2, mine.Count);
        Assert.Equal(QuestionRating.Like, mine[data.Questions[0]]);
        Assert.Equal(QuestionRating.Dislike, mine[data.Questions[1]]);
        Assert.Empty(none);
    }

    [Fact]
    public async Task MostDisliked_OrdersByDislikesAndSkipsUndisliked()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId first = await data.CreateUserAsync(FirstTelegramId, Ct);
        UserId second = await data.CreateUserAsync(SecondTelegramId, Ct);
        UserId third = await data.CreateUserAsync(ThirdTelegramId, Ct);
        await using TrainerDbContext db = data.NewContext();
        QuestionFeedbackService service = CreateService(data, db);
        await service.SetRatingAsync(first, data.Questions[0], QuestionRating.Dislike, Ct);
        await service.SetRatingAsync(second, data.Questions[0], QuestionRating.Dislike, Ct);
        await service.SetRatingAsync(third, data.Questions[0], QuestionRating.Like, Ct);
        await service.SetRatingAsync(first, data.Questions[1], QuestionRating.Dislike, Ct);
        await service.SetRatingAsync(first, data.Questions[2], QuestionRating.Like, Ct);
        await service.SetRatingAsync(second, data.Questions[2], QuestionRating.Like, Ct);
        QuestionFeedbackQueries queries = CreateQueries(data.NewContext());

        IReadOnlyList<RatingTotals> all = await queries.GetMostDislikedAsync(10, CultureKey.Default, Ct);
        IReadOnlyList<RatingTotals> top = await queries.GetMostDislikedAsync(1, CultureKey.Default, Ct);

        Assert.Equal([data.Questions[0], data.Questions[1]], all.Select(t => t.QuestionId));
        Assert.Equal((2, 1), (all[0].Dislikes, all[0].Likes));
        Assert.Equal((1, 0), (all[1].Dislikes, all[1].Likes));
        Assert.False(string.IsNullOrWhiteSpace(all[0].Title));
        Assert.False(string.IsNullOrWhiteSpace(all[0].Slug));
        Assert.False(string.IsNullOrWhiteSpace(all[0].Stack));
        Assert.Equal([data.Questions[0]], top.Select(t => t.QuestionId));
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task MostDisliked_TitlesAreInTheRequestedCulture(string cultureValue)
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(FirstTelegramId, Ct);
        await using TrainerDbContext db = data.NewContext();
        await CreateService(data, db).SetRatingAsync(user, data.Questions[0], QuestionRating.Dislike, Ct);
        string questionId = data.Questions[0].Value;
        Dictionary<string, string> titles = await data.Db.QuestionLocales
            .AsNoTracking()
            .Where(l => l.QuestionId == questionId)
            .ToDictionaryAsync(l => l.Culture, l => l.Title, StringComparer.Ordinal, Ct);
        Assert.NotEqual(titles["ru"], titles["en"]);
        QuestionFeedbackQueries queries = CreateQueries(data.NewContext());

        IReadOnlyList<RatingTotals> top = await queries.GetMostDislikedAsync(5, new CultureKey(cultureValue), Ct);

        Assert.Equal(titles[cultureValue], Assert.Single(top).Title);
    }

    [Fact]
    public async Task IsOpenForFeedback_FollowsTheRuleTheServiceEnforces()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        await data.RetireQuestionAsync(data.Questions[0], Ct);
        QuestionEntity inTopic = await data.Db.Questions.AsNoTracking().SingleAsync(q => q.Id == data.Questions[1].Value, Ct);
        DateTimeOffset now = data.Clock.GetUtcNow();
        await data.Db.Topics
            .Where(t => t.StackKey == inTopic.StackKey && t.Key == inTopic.TopicKey)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RetiredAt, now), Ct);
        QuestionEntity open = (await data.Db.Questions.AsNoTracking().ToListAsync(Ct))
            .First(q => q.Id != data.Questions[0].Value && !(q.StackKey == inTopic.StackKey && q.TopicKey == inTopic.TopicKey));
        QuestionFeedbackQueries queries = CreateQueries(data.NewContext());

        Assert.False(await queries.IsOpenForFeedbackAsync(data.Questions[0], Ct));
        Assert.False(await queries.IsOpenForFeedbackAsync(data.Questions[1], Ct));
        Assert.True(await queries.IsOpenForFeedbackAsync(new QuestionId(open.Id), Ct));

        await data.Db.Stacks
            .Where(s => s.Key == open.StackKey)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RetiredAt, now), Ct);
        Assert.False(await CreateQueries(data.NewContext()).IsOpenForFeedbackAsync(new QuestionId(open.Id), Ct));
    }

    [Fact]
    public async Task GetReports_FiltersByResolvedNewestFirstAndPages()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(FirstTelegramId, Ct);
        UserId admin = await data.CreateUserAsync(SecondTelegramId, Ct);
        for (int i = 0; i < 26; i++)
        {
            Assert.Equal(ReportOutcome.Created, (await ReportCombinationAsync(data, user, i, reportsPerDay: 100, comment: "c" + i)).Outcome);
            data.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        List<QuestionReportEntity> stored = await ReportsAsync(data);
        await using (TrainerDbContext db = data.NewContext())
        {
            await CreateService(data, db).ResolveReportAsync(new ReportId(stored[0].Id), admin, Ct);
        }

        QuestionFeedbackQueries queries = CreateQueries(data.NewContext());
        ReportPage firstPage = await queries.GetReportsAsync(resolved: false, page: 1, Ct);
        ReportPage clamped = await queries.GetReportsAsync(resolved: false, page: 99, Ct);
        ReportPage resolvedPage = await queries.GetReportsAsync(resolved: true, page: 1, Ct);

        Assert.Equal((25, 1, 1), (firstPage.Rows.Count, firstPage.Page, firstPage.PageCount));
        ReportRow newest = firstPage.Rows[0];
        Assert.Equal(stored[25].Id, newest.Id.Value);
        Assert.Equal("c25", newest.Comment);
        Assert.Equal(FirstTelegramId, newest.ReporterTelegramId);
        Assert.Equal("user" + FirstTelegramId, newest.ReporterName);
        Assert.False(string.IsNullOrWhiteSpace(newest.Title));
        Assert.Null(newest.ResolvedAt);
        Assert.Equal(stored[1].Id, firstPage.Rows[^1].Id.Value);
        Assert.Equal(1, clamped.Page);
        ReportRow resolvedRow = Assert.Single(resolvedPage.Rows);
        Assert.Equal(stored[0].Id, resolvedRow.Id.Value);
        Assert.NotNull(resolvedRow.ResolvedAt);
    }

    [Fact]
    public async Task GetReports_SameCreatedAt_PagesWithoutOverlapOrGap()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(FirstTelegramId, Ct);
        // The clock never moves, so all 30 reports share one created_at and only the id orders them.
        for (int i = 0; i < 30; i++)
        {
            Assert.Equal(ReportOutcome.Created, (await ReportCombinationAsync(data, user, i, reportsPerDay: 100)).Outcome);
        }

        string[] expected = [.. (await ReportsAsync(data)).Select(r => r.Id).Order(StringComparer.Ordinal).Reverse()];
        QuestionFeedbackQueries queries = CreateQueries(data.NewContext());
        ReportPage first = await queries.GetReportsAsync(resolved: false, page: 1, Ct);
        ReportPage second = await queries.GetReportsAsync(resolved: false, page: 2, Ct);

        Assert.Equal(expected[..25], first.Rows.Select(r => r.Id.Value));
        Assert.Equal(expected[25..], second.Rows.Select(r => r.Id.Value));
    }

    [Fact]
    public async Task GetReports_MoreThanOnePage_CountsPages()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(FirstTelegramId, Ct);
        for (int i = 0; i < 26; i++)
        {
            await ReportCombinationAsync(data, user, i, reportsPerDay: 100);
            data.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        QuestionFeedbackQueries queries = CreateQueries(data.NewContext());
        ReportPage first = await queries.GetReportsAsync(resolved: false, page: 1, Ct);
        ReportPage second = await queries.GetReportsAsync(resolved: false, page: 2, Ct);
        ReportPage empty = await CreateQueries(data.NewContext()).GetReportsAsync(resolved: true, page: 1, Ct);

        Assert.Equal((25, 2), (first.Rows.Count, first.PageCount));
        Assert.Equal((1, 2, 2), (second.Rows.Count, second.Page, second.PageCount));
        Assert.Empty(empty.Rows);
        Assert.Equal(1, empty.PageCount);
    }
}
