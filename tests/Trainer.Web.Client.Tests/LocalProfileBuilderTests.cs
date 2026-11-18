using Trainer.Contracts;
using Trainer.Domain.Scheduling;
using Trainer.Web.Client.Backend;

namespace Trainer.Web.Client.Tests;

public sealed class LocalProfileBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly Sm2ReviewScheduler Scheduler = new();

    private static QuestionDto Q(string topic, string title, string level) =>
        TestQuestions.Open() with { Topic = topic, TopicTitle = title, Level = level };

    private static AnswerDto A(QuestionDto q, string grade, DateTimeOffset at) => StaticSiteFixture.Answer(q, grade, at);

    // Alpha: q1 junior, q2 middle. Beta: q3 middle, q4 middle. qGone is answered but not in the index.
    private static (StaticIndexDto Index, QuestionDto[] Q, QuestionDto Gone) Fixture()
    {
        QuestionDto q1 = Q("alpha", "Alpha", "junior");
        QuestionDto q2 = Q("alpha", "Alpha", "middle");
        QuestionDto q3 = Q("beta", "Beta", "middle");
        QuestionDto q4 = Q("beta", "Beta", "middle");
        QuestionDto gone = Q("alpha", "Alpha", "senior");
        return (new StaticSiteFixture(q1, q2, q3, q4).Index, [q1, q2, q3, q4], gone);
    }

    [Fact]
    public void Build_CountsAnsweredKnownAndDuePerTopicAndLevel()
    {
        (StaticIndexDto index, QuestionDto[] q, _) = Fixture();
        // q1 knew 5 days ago: due since 4 days. q2 knew an hour ago: due in 23 hours. q3 did not know 2 days ago: due since 1 day.
        LocalProgress progress = LocalProgress.Replay(
            [A(q[0], "knew", Now.AddDays(-5)), A(q[1], "knew", Now.AddHours(-1)), A(q[2], "didnt-know", Now.AddDays(-2))],
            Scheduler,
            Now);

        ProfileDto profile = LocalProfileBuilder.Build(index, progress, [], Now, TimeSpan.Zero);

        TopicProgressDto alpha = Assert.Single(profile.Topics, t => t.Topic == "alpha");
        Assert.Equal((2, 2, 2, 1), (alpha.LiveQuestions, alpha.Answered, alpha.Known, alpha.Due));
        Assert.Equal("Alpha", alpha.Title);
        TopicProgressDto beta = Assert.Single(profile.Topics, t => t.Topic == "beta");
        Assert.Equal((2, 1, 0, 1), (beta.LiveQuestions, beta.Answered, beta.Known, beta.Due));

        LevelProgressDto junior = Assert.Single(profile.Levels, l => l.Level == "junior");
        Assert.Equal((1, 1, 1), (junior.LiveQuestions, junior.Answered, junior.Known));
        LevelProgressDto middle = Assert.Single(profile.Levels, l => l.Level == "middle");
        Assert.Equal((3, 2, 1), (middle.LiveQuestions, middle.Answered, middle.Known));
        Assert.Equal(5, profile.Levels.Count);
        Assert.All(profile.Levels.Where(l => l.Level is "senior" or "lead" or "architect"), l => Assert.Equal((0, 0, 0), (l.LiveQuestions, l.Answered, l.Known)));

        Assert.Equal(3, profile.TotalAnswered);
        Assert.Equal(2, profile.DueNow);
    }

    [Fact]
    public void Build_LiveQuestionsComeFromTheIndex()
    {
        (StaticIndexDto index, QuestionDto[] q, QuestionDto gone) = Fixture();
        LocalProgress progress = LocalProgress.Replay([A(q[0], "knew", Now.AddDays(-1)), A(gone, "knew", Now.AddDays(-1))], Scheduler, Now);

        ProfileDto profile = LocalProfileBuilder.Build(index, progress, [], Now, TimeSpan.Zero);

        Assert.Equal(4, profile.Topics.Sum(t => t.LiveQuestions));
        // The answered question the index does not list is neither answered nor live; its state is kept for other indexes.
        Assert.Equal(1, profile.TotalAnswered);
        Assert.Equal(1, profile.Topics.Sum(t => t.Answered));
        Assert.Equal(2, progress.States.Count);
        Assert.Equal((0, 0), (profile.Levels.Single(l => l.Level == "senior").LiveQuestions, profile.Levels.Single(l => l.Level == "senior").Answered));
    }

    [Fact]
    public void Build_SkipsIndexEntriesWithAnUnknownLevelOrType()
    {
        (StaticIndexDto index, QuestionDto[] q, _) = Fixture();
        StaticIndexEntryDto broken = index.Questions[0] with { Level = "wizard" };
        StaticIndexDto mixed = index with { Questions = [broken, .. index.Questions.Skip(1)] };

        ProfileDto profile = LocalProfileBuilder.Build(mixed, LocalProgress.Empty, [], Now, TimeSpan.Zero);

        Assert.Equal(3, profile.Topics.Sum(t => t.LiveQuestions));
        Assert.DoesNotContain(StaticIndex.LiveIds(mixed), id => id.Value == broken.Id);
        Assert.Equal(q.Length - 1, StaticIndex.Candidates(mixed).Count);
    }

    [Fact]
    public void Build_NextDueIsTheEarliestFutureState()
    {
        (StaticIndexDto index, QuestionDto[] q, _) = Fixture();
        LocalProgress progress = LocalProgress.Replay(
            [A(q[0], "knew", Now.AddHours(-1)), A(q[1], "knew", Now.AddHours(-20)), A(q[2], "knew", Now.AddDays(-9))],
            Scheduler,
            Now);

        ProfileDto profile = LocalProfileBuilder.Build(index, progress, [], Now, TimeSpan.Zero);

        // q1 is due at +23h, q2 at +4h, q3 is already due; the earliest future one is q2.
        Assert.Equal(Now.AddHours(-20).AddDays(1), profile.NextDueAt);
        Assert.Equal(1, profile.DueNow);
    }

    [Fact]
    public void Build_NextDueIsNullWhenNothingIsInTheFuture()
    {
        (StaticIndexDto index, QuestionDto[] q, _) = Fixture();
        LocalProgress progress = LocalProgress.Replay([A(q[0], "knew", Now.AddDays(-9))], Scheduler, Now);

        Assert.Null(LocalProfileBuilder.Build(index, progress, [], Now, TimeSpan.Zero).NextDueAt);
        Assert.Null(LocalProfileBuilder.Build(index, LocalProgress.Empty, [], Now, TimeSpan.Zero).NextDueAt);
    }

    [Fact]
    public void Build_MocksComeNewestFirst_AtMostFive()
    {
        (StaticIndexDto index, _, _) = Fixture();
        MockHistoryDto[] mocks =
        [
            .. Enumerable.Range(1, 7).Select(i => new MockHistoryDto("mock-" + i, "middle", 5, Now.AddDays(-i), 1, 1, 1, 2)),
        ];

        ProfileDto profile = LocalProfileBuilder.Build(index, LocalProgress.Empty, mocks, Now, TimeSpan.Zero);

        Assert.Equal(["mock-1", "mock-2", "mock-3", "mock-4", "mock-5"], profile.RecentMocks.Select(m => m.Id));
    }

    [Fact]
    public void Build_StreakAndTotalsFollowTheDeviceOffset()
    {
        (StaticIndexDto index, QuestionDto[] q, _) = Fixture();
        LocalProgress progress = LocalProgress.Replay(
            [A(q[0], "knew", new DateTimeOffset(2026, 11, 10, 20, 30, 0, TimeSpan.Zero)), A(q[1], "knew", new DateTimeOffset(2026, 11, 10, 21, 30, 0, TimeSpan.Zero))],
            Scheduler,
            Now);
        DateTimeOffset now = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

        Assert.Equal(2, LocalProfileBuilder.Build(index, progress, [], now, TimeSpan.FromHours(3)).StreakDays);
        Assert.Equal(1, LocalProfileBuilder.Build(index, progress, [], now, TimeSpan.Zero).StreakDays);
    }

    [Fact]
    public void Summarize_GivesTheSidebarTheSameNumbersAsTheProfile()
    {
        (StaticIndexDto index, QuestionDto[] q, _) = Fixture();
        LocalProgress progress = LocalProgress.Replay([A(q[0], "knew", Now.AddDays(-5))], Scheduler, Now);

        IslandProgress summary = progress.Summarize(index, Now, TimeSpan.Zero);
        ProfileDto profile = LocalProfileBuilder.Build(index, progress, [], Now, TimeSpan.Zero);

        Assert.Equal(profile.DueNow, summary.DueNow);
        Assert.Equal(profile.StreakDays, summary.StreakDays);
        Assert.Equal(profile.Topics, summary.Topics);
        Assert.Equal(1, summary.DueNow);
    }

    [Fact]
    public void Build_TopicRowsFollowTheServersOrder_UnlistedTopicsLastByKey()
    {
        (StaticIndexDto index, _, _) = Fixture();
        // The index lists alpha before beta by key; the page was rendered with the culture's own order.
        TopicDto[] serverOrder = [new("beta", "Beta"), new("alpha", "Alpha")];

        ProfileDto ordered = LocalProfileBuilder.Build(index, LocalProgress.Empty, [], Now, TimeSpan.Zero, serverOrder);
        ProfileDto partial = LocalProfileBuilder.Build(index, LocalProgress.Empty, [], Now, TimeSpan.Zero, [new TopicDto("beta", "Beta")]);
        ProfileDto none = LocalProfileBuilder.Build(index, LocalProgress.Empty, [], Now, TimeSpan.Zero);

        Assert.Equal(["beta", "alpha"], ordered.Topics.Select(t => t.Topic));
        Assert.Equal(["beta", "alpha"], partial.Topics.Select(t => t.Topic));
        Assert.Equal(["alpha", "beta"], none.Topics.Select(t => t.Topic));
        Assert.Equal(["beta", "alpha"], LocalProgress.Empty.Summarize(index, Now, TimeSpan.Zero, serverOrder).Topics.Select(t => t.Topic));
    }
}
