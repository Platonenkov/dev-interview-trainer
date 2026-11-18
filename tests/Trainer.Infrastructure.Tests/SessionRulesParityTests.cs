using Microsoft.EntityFrameworkCore;
using Trainer.Application.Sessions;
using Trainer.Contracts;
using Trainer.Domain.Grading;
using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;
using Trainer.Domain.Sessions;
using Trainer.Domain.Users;
using Trainer.Infrastructure.Feedback;
using Trainer.Infrastructure.Persistence;
using Trainer.Infrastructure.Persistence.Entities;
using Trainer.Infrastructure.Queries;
using Trainer.Infrastructure.Sessions;
using Trainer.TestSupport;

namespace Trainer.Infrastructure.Tests;

/// <summary>
/// The SQL in <see cref="SessionQueries"/> and the domain rules in <see cref="PracticeSelector"/> and
/// <see cref="ReviewOrder"/> must pick from the same pool; a client that selects locally relies on that.
/// </summary>
public sealed class SessionRulesParityTests(PostgresFixture postgres)
{
    private const long TelegramId = 700_800_900L;

    private static readonly CultureKey En = new("en");
    private static readonly StackKey Dotnet = new("dotnet");

    private static readonly IReadOnlyDictionary<string, CaseDefinition> Definitions = new CaseDefinition[]
    {
        new("all", false, _ => Criteria()),
        new("all-signed-in", true, _ => Criteria()),
        new("level-middle", false, _ => Criteria(levels: [Level.Middle])),
        new("level-junior-and-senior", false, _ => Criteria(levels: [Level.Junior, Level.Senior])),
        new("topic-concurrency", false, _ => Criteria(topics: ["concurrency"])),
        new("type-open-single", false, _ => Criteria(types: [QuestionType.Open, QuestionType.Single])),
        new("level-topic-type", false, _ => Criteria(levels: [Level.Middle], topics: ["concurrency"], types: [QuestionType.Open])),
        new("no-match", false, _ => Criteria(levels: [Level.Senior], topics: ["concurrency"], types: [QuestionType.Single])),
        new("new-anonymous", false, _ => Criteria(status: PracticeStatus.New)),
        new("new-anonymous-exclude", false, q => Criteria(status: PracticeStatus.New, exclude: [q[0], q[5]])),
        new("new-signed-in", true, _ => Criteria(status: PracticeStatus.New)),
        new("new-signed-in-exclude", true, q => Criteria(status: PracticeStatus.New, exclude: [q[0]])),
        new("due-signed-in", true, _ => Criteria(status: PracticeStatus.Due)),
        new("due-signed-in-exclude", true, q => Criteria(status: PracticeStatus.Due, exclude: [q[1]])),
        new("due-signed-in-level", true, _ => Criteria(status: PracticeStatus.Due, levels: [Level.Middle])),
        new("due-anonymous", false, _ => Criteria(status: PracticeStatus.Due)),
        new("exclude-all-status", false, q => Criteria(exclude: [q[0], q[1]])),
        new("first-filtered-out", false, q => Criteria(levels: [Level.Senior], topics: ["concurrency"], first: q[0])),
        new("first-excluded", false, q => Criteria(first: q[0], exclude: [q[0], q[1]])),
        new("first-due-anonymous", false, q => Criteria(status: PracticeStatus.Due, first: q[0])),
        new("first-new-signed-in-answered", true, q => Criteria(status: PracticeStatus.New, first: q[1])),
        new("first-due-signed-in", true, q => Criteria(status: PracticeStatus.Due, first: q[3], exclude: [q[1]])),
        new("first-not-live", false, q => Criteria(first: q[5])),
        new("truncated", false, _ => Criteria(count: 2)),
        new("truncated-first", false, q => Criteria(first: q[2], count: 2)),
        new("truncated-first-filtered-out", false, q => Criteria(levels: [Level.Senior], first: q[0], count: 1)),
        new("truncated-due-signed-in-first", true, q => Criteria(status: PracticeStatus.Due, first: q[0], count: 2)),
    }.ToDictionary(d => d.Name, StringComparer.Ordinal);

    public static TheoryData<string> Cases => [.. Definitions.Keys];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PracticePool_SqlAndDomainAgree(string name)
    {
        CaseDefinition definition = Definitions[name];
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        await AddReviewStatesAsync(data, user);
        // The retired question is not live for either implementation, even as the first question.
        await data.RetireQuestionAsync(data.Questions[5], Ct);
        await using TrainerDbContext db = data.NewContext();
        PracticeCriteria criteria = definition.Build(data.Questions);
        UserId? caller = definition.SignedIn ? user : null;

        SessionDto sql = await new SessionQueries(db, data.Clock, new MockComposer(new Random(5)), new QuestionFeedbackQueries(db))
            .GetPracticeAsync(criteria, caller, Ct);

        IReadOnlyList<PracticeCandidate> live = await LiveCandidatesAsync(db);
        Dictionary<QuestionId, DateTimeOffset>? states = caller is null ? null : await StatesAsync(db, user);
        PracticeSelection selection = new(criteria.Levels, criteria.Topics, criteria.Types, criteria.Status, criteria.Count, criteria.First, criteria.Exclude);
        PracticeSelector selector = new(new Random(5));
        PracticePick domain = selector.Select(live, selection, states, data.Clock.GetUtcNow());
        PracticePick wholePool = selector.Select(live, selection with { Count = SessionLimits.MaxCount }, states, data.Clock.GetUtcNow());

        // Count 50 returns the whole pool, so both id sets are the pool itself.
        SessionDto sqlWhole = await new SessionQueries(db, data.Clock, new MockComposer(new Random(5)), new QuestionFeedbackQueries(db))
            .GetPracticeAsync(criteria with { Count = SessionLimits.MaxCount }, caller, Ct);
        Assert.Equal(Sorted(sqlWhole.Questions.Select(q => q.Id)), Sorted(wholePool.Ids.Select(i => i.Value)));
        Assert.Equal(sqlWhole.PoolSize, wholePool.PoolSize);
        Assert.Equal(Math.Min(criteria.Count, wholePool.PoolSize), sql.Questions.Count);
        // A truncated session still draws only from the pool; an untruncated one is the pool.
        Assert.All(domain.Ids, id => Assert.Contains(id, wholePool.Ids));
        if (criteria.Count >= wholePool.PoolSize)
        {
            Assert.Equal(Sorted(sql.Questions.Select(q => q.Id)), Sorted(domain.Ids.Select(i => i.Value)));
        }

        Assert.All(sql.Questions, q => Assert.Contains(new QuestionId(q.Id), wholePool.Ids));
        Assert.Equal(sql.PoolSize, domain.PoolSize);
        Assert.Equal(sql.Questions.Count, domain.Ids.Count);
        if (criteria.First is { } first && domain.Ids.Count > 0 && live.Any(c => c.Id == first))
        {
            Assert.Equal(first.Value, sql.Questions[0].Id);
            Assert.Equal(first, domain.Ids[0]);
        }
    }

    [Fact]
    public async Task ReviewOrder_SqlAndDomainAgree()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        UserId user = await data.CreateUserAsync(TelegramId, Ct);
        await AddReviewStatesAsync(data, user);
        // Equal due times, one overdue state of a retired question and one future state exercise tie order and the live filter.
        await AddStateAsync(data, user, data.Questions[0], TimeSpan.FromHours(-1));
        await data.RetireQuestionAsync(data.Questions[4], Ct);
        await using TrainerDbContext db = data.NewContext();

        SessionDto sql = await new SessionQueries(db, data.Clock, new MockComposer(new Random(5)), new QuestionFeedbackQueries(db))
            .GetReviewAsync(new ReviewCriteria(En, Dotnet, 50), user, Ct);

        IReadOnlyList<PracticeCandidate> live = await LiveCandidatesAsync(db);
        Dictionary<QuestionId, DateTimeOffset> states = await StatesAsync(db, user);
        (IReadOnlyList<QuestionId> ids, int poolSize) = ReviewOrder.Pick(
            states.Select(s => (s.Key, s.Value)),
            live.Select(c => c.Id).ToHashSet(),
            data.Clock.GetUtcNow(),
            50);

        Assert.NotEmpty(ids);
        Assert.Equal(sql.Questions.Select(q => q.Id), ids.Select(i => i.Value));
        Assert.Equal(sql.PoolSize, poolSize);
    }

    private static PracticeCriteria Criteria(
        Level[]? levels = null,
        string[]? topics = null,
        QuestionType[]? types = null,
        PracticeStatus status = PracticeStatus.All,
        QuestionId? first = null,
        QuestionId[]? exclude = null,
        int count = SessionLimits.MaxCount) =>
        new(En, Dotnet, levels ?? [], [.. (topics ?? []).Select(t => new TopicKey(t))], types ?? [], status, count, first, exclude ?? []);

    private static string[] Sorted(IEnumerable<string> ids) => [.. ids.Order(StringComparer.Ordinal)];

    private static async Task<IReadOnlyList<PracticeCandidate>> LiveCandidatesAsync(TrainerDbContext db)
    {
        var rows = await LiveQuestions.Of(db, En, Dotnet).Select(q => new { q.Id, q.Level, q.TopicKey, q.Type }).ToListAsync(Ct);
        return [.. rows.Select(r => new PracticeCandidate(new QuestionId(r.Id), r.Level, new TopicKey(r.TopicKey), r.Type))];
    }

    private static async Task<Dictionary<QuestionId, DateTimeOffset>> StatesAsync(TrainerDbContext db, UserId user)
    {
        string userId = user.Value;
        var rows = await db.ReviewStates.AsNoTracking().Where(r => r.UserId == userId).Select(r => new { r.QuestionId, r.DueAt }).ToListAsync(Ct);
        return rows.ToDictionary(r => new QuestionId(r.QuestionId), r => r.DueAt);
    }

    // Fixture question indexes: 0 closure, 1 configure-await, 2 result-deadlock, 3 thread-safe, 4 parallel-race, 5 starvation.
    private static async Task AddReviewStatesAsync(LearningDatabase data, UserId user)
    {
        await AddStateAsync(data, user, data.Questions[1], TimeSpan.FromHours(-1));
        await AddStateAsync(data, user, data.Questions[2], TimeSpan.Zero);
        await AddStateAsync(data, user, data.Questions[3], TimeSpan.FromMinutes(1));
        await AddStateAsync(data, user, data.Questions[4], TimeSpan.FromDays(-3));
        await AddStateAsync(data, user, data.Questions[5], TimeSpan.FromDays(-4));
    }

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

    private sealed record CaseDefinition(string Name, bool SignedIn, Func<IReadOnlyList<QuestionId>, PracticeCriteria> Build);
}
