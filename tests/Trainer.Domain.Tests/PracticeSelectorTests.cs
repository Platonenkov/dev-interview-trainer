using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;
using Trainer.Domain.Sessions;

namespace Trainer.Domain.Tests;

public sealed class PracticeSelectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly TopicKey Async = new("async");

    private static QuestionId Id(int n) => new($"Q{n:D3}");

    private static PracticeCandidate Candidate(int n, Level level = Level.Junior, string topic = "async", QuestionType type = QuestionType.Open) =>
        new(Id(n), level, new TopicKey(topic), type);

    private static PracticeCandidate[] Live(int count) => [.. Enumerable.Range(1, count).Select(n => Candidate(n))];

    private static PracticeSelection Selection(
        int count = 50,
        PracticeStatus status = PracticeStatus.All,
        Level[]? levels = null,
        QuestionId? first = null,
        QuestionId[]? exclude = null) =>
        new(levels ?? [], [], [], status, count, first, exclude ?? []);

    private static PracticePick Select(
        IReadOnlyList<PracticeCandidate> live,
        PracticeSelection selection,
        IReadOnlyDictionary<QuestionId, DateTimeOffset>? states = null,
        int seed = 3) =>
        new PracticeSelector(new Random(seed)).Select(live, selection, states, Now);

    private static QuestionId[] Sorted(PracticePick pick) => [.. pick.Ids.OrderBy(i => i.Value, StringComparer.Ordinal)];

    [Fact]
    public void Select_EmptyFilters_PoolIsEveryLiveQuestion()
    {
        PracticeCandidate[] live = Live(10);

        PracticePick pick = Select(live, Selection(count: 5));

        Assert.Equal(5, pick.Ids.Count);
        Assert.Equal(5, pick.Ids.Distinct().Count());
        Assert.All(pick.Ids, id => Assert.Contains(live, c => c.Id == id));
        Assert.Equal(10, pick.PoolSize);
    }

    [Fact]
    public void Select_FirstComesFirstEvenWhenFilteredOutOrExcluded()
    {
        List<PracticeCandidate> live = [.. Live(6), Candidate(7, Level.Senior)];

        PracticePick pick = Select(live, Selection(levels: [Level.Junior], first: Id(7), exclude: [Id(7), Id(1)]));

        Assert.Equal(Id(7), pick.Ids[0]);
        // Five juniors remain after the exclusion, plus the first question.
        Assert.Equal(6, pick.PoolSize);
        Assert.Equal(6, pick.Ids.Count);
        Assert.DoesNotContain(Id(1), pick.Ids);
    }

    [Fact]
    public void Select_FirstCountsTowardCount()
    {
        PracticePick pick = Select(Live(10), Selection(count: 3, first: Id(4)));

        Assert.Equal(3, pick.Ids.Count);
        Assert.Equal(Id(4), pick.Ids[0]);
        Assert.Single(pick.Ids, id => id == Id(4));
        Assert.Equal(10, pick.PoolSize);
    }

    [Fact]
    public void Select_FirstNotLive_IsIgnored()
    {
        PracticeCandidate[] live = Live(4);

        PracticePick without = Select(live, Selection());
        PracticePick with = Select(live, Selection(first: Id(99)));

        Assert.Equal(without.Ids.Count, with.Ids.Count);
        Assert.Equal(without.PoolSize, with.PoolSize);
        Assert.DoesNotContain(Id(99), with.Ids);
    }

    [Fact]
    public void Select_NewWithoutStates_UsesOnlyExclude()
    {
        PracticePick pick = Select(Live(6), Selection(status: PracticeStatus.New, exclude: [Id(2), Id(5)]));

        Assert.Equal([Id(1), Id(3), Id(4), Id(6)], Sorted(pick));
        Assert.Equal(4, pick.PoolSize);
    }

    [Fact]
    public void Select_NewWithStates_LeavesOutQuestionsWithAState()
    {
        Dictionary<QuestionId, DateTimeOffset> states = new()
        {
            [Id(2)] = Now.AddDays(3),
            [Id(3)] = Now.AddDays(-3),
        };

        PracticePick pick = Select(Live(5), Selection(status: PracticeStatus.New), states);

        Assert.Equal([Id(1), Id(4), Id(5)], Sorted(pick));
        Assert.Equal(3, pick.PoolSize);
    }

    [Fact]
    public void Select_DueWithoutStates_MatchesNothingButFirst()
    {
        PracticeCandidate[] live = Live(4);

        PracticePick none = Select(live, Selection(status: PracticeStatus.Due));
        PracticePick onlyFirst = Select(live, Selection(status: PracticeStatus.Due, first: Id(2)));

        Assert.Empty(none.Ids);
        Assert.Equal(0, none.PoolSize);
        Assert.Equal([Id(2)], onlyFirst.Ids);
        Assert.Equal(1, onlyFirst.PoolSize);
    }

    [Fact]
    public void Select_Due_MatchesStatesDueAtOrBeforeNow()
    {
        Dictionary<QuestionId, DateTimeOffset> states = new()
        {
            [Id(1)] = Now.AddSeconds(-1),
            [Id(2)] = Now,
            [Id(3)] = Now.AddSeconds(1),
        };

        PracticePick pick = Select(Live(4), Selection(status: PracticeStatus.Due), states);

        Assert.Equal([Id(1), Id(2)], Sorted(pick));
        Assert.Equal(2, pick.PoolSize);
    }

    [Fact]
    public void Select_ExcludeAppliesToDue()
    {
        Dictionary<QuestionId, DateTimeOffset> states = new() { [Id(1)] = Now.AddDays(-1), [Id(2)] = Now.AddDays(-1) };

        PracticePick pick = Select(Live(3), Selection(status: PracticeStatus.Due, exclude: [Id(1)]), states);

        Assert.Equal([Id(2)], pick.Ids);
        Assert.Equal(1, pick.PoolSize);
    }

    [Fact]
    public void Select_TopicAndTypeFiltersNarrowThePool()
    {
        PracticeCandidate[] live =
        [
            Candidate(1, topic: "async", type: QuestionType.Open),
            Candidate(2, topic: "async", type: QuestionType.Single),
            Candidate(3, topic: "linq", type: QuestionType.Single),
        ];
        PracticeSelection selection = new([], [Async], [QuestionType.Single], PracticeStatus.All, 10, null, []);

        PracticePick pick = Select(live, selection);

        Assert.Equal([Id(2)], pick.Ids);
        Assert.Equal(1, pick.PoolSize);
    }

    [Fact]
    public void Select_IsUniformAcrossSeeds()
    {
        PracticeCandidate[] live = Live(4);
        Dictionary<QuestionId, int> hits = live.ToDictionary(c => c.Id, _ => 0);

        for (int seed = 0; seed < 2000; seed++)
        {
            PracticePick pick = Select(live, Selection(count: 1), seed: seed);
            hits[pick.Ids.Single()]++;
        }

        Assert.All(hits.Values, h => Assert.InRange(h, 400, 600));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void Select_CountOutOfRange_Throws(int count) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Select(Live(3), Selection(count: count)));

    [Fact]
    public void Select_TooManyExcluded_Throws()
    {
        QuestionId[] exclude = [.. Enumerable.Range(0, SessionLimits.MaxExcludedQuestions + 1).Select(n => new QuestionId($"X{n}"))];

        Assert.Throws<ArgumentException>(() => Select(Live(3), Selection(exclude: exclude)));
    }
}
