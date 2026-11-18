using System.Net;
using Trainer.Contracts;
using Trainer.Domain.Questions;
using Trainer.Web.Client.Api;
using Trainer.Web.Client.Backend;
using Trainer.Web.Client.Sessions;
using Trainer.Web.Client.Storage;

namespace Trainer.Web.Client.Tests;

public sealed class StaticIslandBackendTests
{
    private static readonly DateTimeOffset Now = StaticSiteFixture.Start;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static QuestionDto Q(string level = "middle", string topic = "closures", string type = "open") =>
        TestQuestions.Open(type) with { Level = level, Topic = topic, TopicTitle = topic };

    private static PracticeSessionRequest Practice(string status = "all", int count = 20, string? first = null, string[]? levels = null, string[]? exclude = null) =>
        new("en", "dotnet", levels ?? [], [], [], status, count, first, exclude ?? []);

    private static IslandBootstrapDto Bootstrap(StaticSiteFixture site, MeDto? me = null) =>
        System.Text.Json.JsonSerializer.Deserialize(IslandHarness.BootstrapJson(me, StaticSiteFixture.Site), TrainerJsonContext.Default.IslandBootstrapDto)!;

    private static async Task Answer(StaticSiteFixture.Tab tab, QuestionDto question, string grade, DateTimeOffset at) =>
        await tab.Outbox.AddAsync(StorageKeys.AnonymousOutbox, StaticSiteFixture.Answer(question, grade, at));

    [Fact]
    public async Task Practice_FetchesTheIndexOnceAndOnlyTheFirstQuestion()
    {
        QuestionDto[] questions = [Q(), Q(), Q(), Q()];
        StaticSiteFixture site = new(questions);
        StaticSiteFixture.Tab tab = site.NewTab();

        ApiResult<SessionPlan> result = await tab.Backend.PracticeAsync(Bootstrap(site), Practice(), Ct);
        SessionPlan plan = result.Value!;
        PracticeSession session = new(plan, new Trainer.Domain.Ids.UlidGenerator(TimeProvider.System), new Random(1), new ServerClock(TimeProvider.System, new FakeBrowserStore()));
        await session.StartAsync(Ct);

        QuestionDto first = questions.Single(q => q.Id == plan.QuestionIds[0]);
        QuestionDto second = questions.Single(q => q.Id == plan.QuestionIds[1]);
        Assert.Equal(
            ["/" + StaticSiteFixture.IndexPath, StaticSiteFixture.QuestionPath(first), StaticSiteFixture.QuestionPath(second)],
            site.Handler.Requests.Select(r => r.Path));
        Assert.Equal(4, plan.QuestionIds.Count);
    }

    [Fact]
    public async Task Practice_NewLeavesOutLocallyAnsweredQuestionsWithoutExclude()
    {
        QuestionDto[] questions = [Q(), Q(), Q(), Q()];
        StaticSiteFixture site = new(questions);
        StaticSiteFixture.Tab tab = site.NewTab();
        await Answer(tab, questions[0], "knew", Now.AddDays(-1));
        await Answer(tab, questions[1], "partial", Now.AddHours(-3));

        ApiResult<SessionPlan> result = await tab.Backend.PracticeAsync(Bootstrap(site), Practice("new", exclude: []), Ct);

        Assert.False(tab.Backend.SendsAnsweredAsExclude);
        Assert.Equal(new[] { questions[2].Id, questions[3].Id }.Order(StringComparer.Ordinal), result.Value!.QuestionIds.Order(StringComparer.Ordinal));
        Assert.Equal(2, result.Value.PoolSize);
    }

    [Fact]
    public async Task Practice_DueUsesLocalReviewStates()
    {
        QuestionDto[] questions = [Q(), Q(), Q()];
        StaticSiteFixture site = new(questions);
        StaticSiteFixture.Tab tab = site.NewTab();
        await Answer(tab, questions[0], "knew", Now.AddDays(-5));
        await Answer(tab, questions[1], "knew", Now.AddMinutes(-5));

        ApiResult<SessionPlan> result = await tab.Backend.PracticeAsync(Bootstrap(site), Practice("due"), Ct);

        Assert.Equal([questions[0].Id], result.Value!.QuestionIds);
    }

    [Fact]
    public async Task Practice_DueStatusWithoutHistory_IsEmpty()
    {
        StaticSiteFixture site = new(Q(), Q());
        StaticSiteFixture.Tab tab = site.NewTab();

        ApiResult<SessionPlan> result = await tab.Backend.PracticeAsync(Bootstrap(site), Practice("due"), Ct);

        Assert.Empty(result.Value!.QuestionIds);
        Assert.Equal(IslandStateKind.Empty, IslandState.FromResult(result, result.Value.QuestionIds.Count == 0));
    }

    [Fact]
    public async Task Practice_FirstComesFirstWhateverTheFilters()
    {
        QuestionDto junior = Q("junior");
        QuestionDto[] seniors = [Q("senior"), Q("senior"), Q("senior")];
        StaticSiteFixture site = new([junior, .. seniors]);
        StaticSiteFixture.Tab tab = site.NewTab();
        await Answer(tab, junior, "knew", Now.AddDays(-1));

        ApiResult<SessionPlan> result = await tab.Backend.PracticeAsync(Bootstrap(site), Practice("new", first: junior.Id, levels: ["senior"]), Ct);

        SessionPlan plan = result.Value!;
        Assert.Equal([junior.Id], plan.QuestionIds);
        SessionRemainder rest = await plan.Remainder!;
        Assert.Equal(seniors.Select(q => q.Id).Order(StringComparer.Ordinal), rest.QuestionIds.Order(StringComparer.Ordinal));
        Assert.Equal(4, rest.PoolSize);
        Assert.DoesNotContain(junior.Id, rest.QuestionIds);
    }

    [Fact]
    public async Task Practice_FirstThatIsNotInTheIndexIsIgnored()
    {
        QuestionDto[] questions = [Q(), Q()];
        StaticSiteFixture site = new(questions);
        StaticSiteFixture.Tab tab = site.NewTab();

        ApiResult<SessionPlan> result = await tab.Backend.PracticeAsync(Bootstrap(site), Practice(first: "01JZZZZZZZZZZZZZZZZZZZZZZZ"), Ct);

        Assert.Null(result.Value!.Remainder);
        Assert.Equal(2, result.Value.QuestionIds.Count);
    }

    [Fact]
    public async Task Practice_FirstSessionStartsWithoutProgressAndCompletesWhenItIsReady()
    {
        QuestionDto[] questions = [Q(), Q(), Q()];
        StaticSiteFixture site = new(questions);
        StaticSiteFixture.Tab tab = site.NewTab();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        tab.Hook.ReadGate = _ => release.Task;

        ApiResult<SessionPlan> result = await tab.Backend.PracticeAsync(Bootstrap(site), Practice(first: questions[1].Id), Ct);
        PracticeSession session = new(result.Value!, new Trainer.Domain.Ids.UlidGenerator(TimeProvider.System), new Random(1), new ServerClock(TimeProvider.System, new FakeBrowserStore()));
        await session.StartAsync(Ct);

        Assert.True(session.SelectionPending);
        Assert.Equal(questions[1].Id, session.Current!.Question.Id);
        Assert.Equal(1, session.Count);

        release.SetResult();
        await session.CompleteSelectionAsync(Ct);

        Assert.False(session.SelectionPending);
        Assert.Equal(3, session.Count);
        Assert.False(session.IsFinished);
        // The second question is prefetched as soon as it is known.
        Assert.Equal(2, site.QuestionRequests.Count);
    }

    [Fact]
    public async Task Mock_FetchesExactlyTheChosenQuestions()
    {
        QuestionDto[] questions = [.. Enumerable.Range(0, 6).Select(_ => Q("middle")), .. Enumerable.Range(0, 6).Select(_ => Q("junior"))];
        StaticSiteFixture site = new(questions);
        StaticSiteFixture.Tab tab = site.NewTab();

        ApiResult<MockSessionDto> result = await tab.Backend.MockAsync(Bootstrap(site), new MockSessionRequest("en", "dotnet", "middle", [], 5), Ct);

        MockSessionDto mock = result.Value!;
        Assert.Equal(5, mock.Questions.Count);
        Assert.Equal(12, mock.PoolSize);
        // 12 questions are fewer than three times the 5 asked for, so the visitor is warned before the mock starts.
        Assert.True(mock.RepeatsLikely);
        Assert.Single(site.IndexRequests);
        Assert.Equal(
            mock.Questions.Select(q => StaticSiteFixture.QuestionPath(q)).Order(StringComparer.Ordinal),
            site.QuestionRequests.Order(StringComparer.Ordinal));
        Assert.Equal(5, site.QuestionRequests.Distinct().Count());
    }

    [Fact]
    public async Task Mock_FetchesAtMostSixFilesAtATime()
    {
        QuestionDto[] questions = [.. Enumerable.Range(0, 20).Select(_ => Q("middle"))];
        StaticSiteFixture site = new(questions);
        StaticSiteFixture.Tab tab = site.NewTab();
        int active = 0;
        int peak = 0;
        site.Handler.Gate = async request =>
        {
            if (!request.Path.Contains("/q/", StringComparison.Ordinal))
            {
                return;
            }

            int now = Interlocked.Increment(ref active);
            int seen;
            while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen)
            {
            }

            await Task.Delay(25, Ct);
            Interlocked.Decrement(ref active);
        };

        ApiResult<MockSessionDto> result = await tab.Backend.MockAsync(Bootstrap(site), new MockSessionRequest("en", "dotnet", "middle", [], 20), Ct);

        Assert.Equal(20, result.Value!.Questions.Count);
        Assert.InRange(peak, 2, 6);
    }

    [Fact]
    public async Task Mock_AFileThatIsGoneIsSiteUpdated()
    {
        QuestionDto[] questions = [.. Enumerable.Range(0, 6).Select(_ => Q("middle"))];
        StaticSiteFixture site = new(questions);
        StaticSiteFixture.Tab tab = site.NewTab();
        Func<RecordedRequest, HttpResponseMessage?> serve = site.Handler.Respond;
        site.Handler.Respond = request => request.Path.Contains("/q/", StringComparison.Ordinal) && request.Path.Contains(questions[3].Id, StringComparison.Ordinal)
            ? FakeHttpHandler.Error(HttpStatusCode.NotFound, ApiErrorCodes.NotFound)
            : serve(request);

        ApiResult<MockSessionDto> result = await tab.Backend.MockAsync(Bootstrap(site), new MockSessionRequest("en", "dotnet", "middle", [], 6), Ct);

        Assert.Null(result.Value);
        Assert.Equal(IslandStateKind.SiteUpdated, IslandState.FromResult(result, false));
    }

    [Fact]
    public async Task Mock_EmptyPoolIsAnEmptyMock()
    {
        StaticSiteFixture site = new(Q("junior"));
        StaticSiteFixture.Tab tab = site.NewTab();

        ApiResult<MockSessionDto> result = await tab.Backend.MockAsync(Bootstrap(site), new MockSessionRequest("en", "dotnet", "architect", [], 5), Ct);

        Assert.Empty(result.Value!.Questions);
        Assert.Equal(0, result.Value.SuggestedCount);
        Assert.Empty(site.QuestionRequests);
    }

    [Fact]
    public void Review_AnswersGoToTheAnonymousOutbox_ForEveryVisitor()
    {
        StaticSiteFixture site = new(Q());
        StaticSiteFixture.Tab tab = site.NewTab();

        Assert.Equal("dit.outbox.v1.anon", tab.Backend.OutboxKey(Bootstrap(site)));
        Assert.Equal("dit.outbox.v1.anon", tab.Backend.OutboxKey(Bootstrap(site, new MeDto(42, 0, 0, []))));
        Assert.True(tab.Backend.KeepsProgress(Bootstrap(site)));
        Assert.False(tab.Backend.FiltersPendingAnswers);
    }

    [Fact]
    public async Task Review_DueQuestionAnsweredEarlierIsOffered()
    {
        QuestionDto answered = Q();
        QuestionDto other = Q();
        StaticSiteFixture site = new(answered, other);
        StaticSiteFixture.Tab tab = site.NewTab();
        // Answered 3 days ago, so due again since 2 days, and the answer is still in the outbox: the replay applied it already.
        await Answer(tab, answered, "knew", Now.AddDays(-3));

        ApiResult<SessionPlan> result = await tab.Backend.ReviewAsync(Bootstrap(site), new ReviewSessionRequest("en", "dotnet", 20), Ct);

        Assert.Equal([answered.Id], result.Value!.QuestionIds);
        Assert.Equal(1, result.Value.PoolSize);
    }

    [Fact]
    public async Task Review_OrdersMostOverdueFirst_TiesById()
    {
        QuestionDto[] q = [Q(), Q(), Q(), Q()];
        StaticSiteFixture site = new(q);
        StaticSiteFixture.Tab tab = site.NewTab();
        await Answer(tab, q[0], "knew", Now.AddDays(-4));
        await Answer(tab, q[1], "knew", Now.AddDays(-9));
        await Answer(tab, q[2], "knew", Now.AddDays(-4));
        await Answer(tab, q[3], "knew", Now.AddMinutes(-5));

        ApiResult<SessionPlan> result = await tab.Backend.ReviewAsync(Bootstrap(site), new ReviewSessionRequest("en", "dotnet", 20), Ct);

        string[] tied = [q[0].Id, q[2].Id];
        Array.Sort(tied, StringComparer.Ordinal);
        Assert.Equal([q[1].Id, .. tied], result.Value!.QuestionIds);
    }

    [Fact]
    public async Task Review_IgnoresStatesOfQuestionsTheIndexDoesNotList()
    {
        QuestionDto listed = Q();
        QuestionDto unlisted = Q();
        StaticSiteFixture site = new(listed);
        StaticSiteFixture.Tab tab = site.NewTab();
        await Answer(tab, listed, "knew", Now.AddDays(-5));
        await Answer(tab, unlisted, "knew", Now.AddDays(-5));

        ApiResult<SessionPlan> result = await tab.Backend.ReviewAsync(Bootstrap(site), new ReviewSessionRequest("en", "dotnet", 20), Ct);

        Assert.Equal([listed.Id], result.Value!.QuestionIds);
    }

    [Fact]
    public async Task Progress_SummarisesTheReplayedLog()
    {
        QuestionDto[] q = [Q(topic: "alpha"), Q(topic: "beta")];
        StaticSiteFixture site = new(q);
        StaticSiteFixture.Tab tab = site.NewTab();
        await Answer(tab, q[0], "knew", Now.AddDays(-5));
        await Answer(tab, q[1], "knew", Now.AddMinutes(-5));

        IslandProgress? progress = await tab.Backend.GetProgressAsync(Bootstrap(site), Ct);

        Assert.NotNull(progress);
        Assert.Equal(1, progress.DueNow);
        Assert.Equal(1, progress.StreakDays);
        Assert.Equal(2, progress.Topics.Count);
    }

    [Fact]
    public async Task Progress_IsNullWhenTheIndexCannotBeLoaded_AndTheNextCallRetries()
    {
        QuestionDto q = Q();
        StaticSiteFixture site = new(q);
        StaticSiteFixture.Tab tab = site.NewTab();
        Func<RecordedRequest, HttpResponseMessage?> serve = site.Handler.Respond;
        bool online = false;
        site.Handler.Respond = request => online ? serve(request) : throw new HttpRequestException("offline");

        Assert.Null(await tab.Backend.GetProgressAsync(Bootstrap(site), Ct));
        ApiResult<SessionPlan> offline = await tab.Backend.PracticeAsync(Bootstrap(site), Practice(), Ct);
        Assert.Equal(IslandStateKind.Offline, IslandState.FromResult(offline, false));

        online = true;
        ApiResult<SessionPlan> back = await tab.Backend.PracticeAsync(Bootstrap(site), Practice(), Ct);
        Assert.Equal([q.Id], back.Value!.QuestionIds);
    }

    [Fact]
    public async Task Profile_IsBuiltFromTheIndexAndTheLog()
    {
        QuestionDto[] q = [Q(), Q()];
        StaticSiteFixture site = new(q);
        StaticSiteFixture.Tab tab = site.NewTab();
        await Answer(tab, q[0], "knew", Now.AddDays(-2));

        ApiResult<ProfileDto> result = await tab.Backend.ProfileAsync(Bootstrap(site), Ct);

        ProfileDto profile = result.Value!;
        Assert.Equal(1, profile.TotalAnswered);
        Assert.Equal(1, profile.DueNow);
        Assert.Empty(profile.RecentMocks);
        Assert.Equal(2, profile.Topics.Sum(t => t.LiveQuestions));
    }

    [Fact]
    public async Task Profile_ListsStoredMockHistoryNewestFirst()
    {
        StaticSiteFixture site = new(Q());
        StaticSiteFixture.Tab tab = site.NewTab();
        MockHistoryDto oldest = new("01KD0000000000000000000001", "junior", 5, Now.AddDays(-3), 1, 1, 1, 2);
        MockHistoryDto newest = new("01KD0000000000000000000002", "senior", 10, Now.AddHours(-1), 6, 2, 1, 1);
        MockHistoryDto middle = new("01KD0000000000000000000003", "middle", 7, Now.AddDays(-1), 4, 1, 1, 1);
        foreach (MockHistoryDto mock in new[] { middle, oldest, newest })
        {
            await tab.History.AddAsync("dotnet", mock);
        }

        // Another tab of the same browser reads what this one stored.
        StaticSiteFixture.Tab other = site.NewTab();
        ApiResult<ProfileDto> result = await other.Backend.ProfileAsync(Bootstrap(site), Ct);

        Assert.Equal([newest, middle, oldest], result.Value!.RecentMocks);
    }

    [Fact]
    public async Task OnMockFinished_StoresTheSummaryAsHistoryOnce_WhateverTheNumberOfHandOvers()
    {
        StaticSiteFixture site = new(Q());
        StaticSiteFixture.Tab tab = site.NewTab();
        MockSummaryDto summary = new(
            "dotnet",
            "middle",
            6,
            false,
            Now.AddMinutes(-30),
            Now.AddMinutes(-5),
            [new MockTopicResultDto("closures", 2, 1, 1, 0), new MockTopicResultDto("linq", 1, 0, 0, 1)]);

        await tab.Backend.OnMockFinishedAsync(Bootstrap(site), "01KD0000000000000000000009", summary, Ct);
        await tab.Backend.OnMockFinishedAsync(Bootstrap(site), "01KD0000000000000000000009", summary, Ct);

        ApiResult<ProfileDto> result = await tab.Backend.ProfileAsync(Bootstrap(site), Ct);
        MockHistoryDto mock = Assert.Single(result.Value!.RecentMocks);
        Assert.Equal(new MockHistoryDto("01KD0000000000000000000009", "middle", 6, Now.AddMinutes(-5), 3, 1, 1, 1), mock);
    }

    [Fact]
    public async Task Profile_ListsOnlyTheMocksOfItsOwnStack()
    {
        StaticSiteFixture site = new(Q());
        StaticSiteFixture.Tab tab = site.NewTab();
        MockSummaryDto Summary(string stack, int knew) =>
            new(stack, "middle", 4, false, Now.AddMinutes(-30), Now.AddMinutes(-5), [new MockTopicResultDto("closures", knew, 0, 0, 4 - knew)]);
        await tab.Backend.OnMockFinishedAsync(Bootstrap(site), "01KD0000000000000000000011", Summary("dotnet", 3), Ct);
        await tab.Backend.OnMockFinishedAsync(Bootstrap(site), "01KD0000000000000000000012", Summary("java", 1), Ct);

        ApiResult<ProfileDto> result = await tab.Backend.ProfileAsync(Bootstrap(site), Ct);

        MockHistoryDto mock = Assert.Single(result.Value!.RecentMocks);
        Assert.Equal("01KD0000000000000000000011", mock.Id);
        Assert.Equal(3, mock.Knew);
    }

    [Fact]
    public async Task ABootstrapWithoutAStaticSite_IsAServerError()
    {
        StaticSiteFixture site = new(Q());
        StaticSiteFixture.Tab tab = site.NewTab();
        IslandBootstrapDto bare = Bootstrap(site) with { StaticSite = null };

        ApiResult<SessionPlan> result = await tab.Backend.PracticeAsync(bare, Practice(), Ct);

        Assert.Equal(500, result.Status);
        Assert.Empty(site.Handler.Requests);
    }

    [Fact]
    public async Task OnSessionFinished_DoesNothing()
    {
        StaticSiteFixture site = new(Q());
        StaticSiteFixture.Tab tab = site.NewTab();

        await tab.Backend.OnSessionFinishedAsync(Bootstrap(site), Ct);

        Assert.Empty(site.Handler.Requests);
        Assert.Equal(0, site.Store.Writes);
    }

    [Fact]
    public void RemainderOf_RemovesTheOpeningQuestionById_WhereverItSits()
    {
        QuestionId a = new(TestQuestions.NewId());
        QuestionId opening = new(TestQuestions.NewId());
        QuestionId c = new(TestQuestions.NewId());

        SessionRemainder rest = StaticIslandBackend.RemainderOf(new Trainer.Domain.Sessions.PracticePick([a, opening, c], 9), opening);

        Assert.Equal([a.Value, c.Value], rest.QuestionIds);
        Assert.Equal(9, rest.PoolSize);
    }

    [Fact]
    public async Task AnIndexFailure_LeavesNoUnobservedProgressException()
    {
        StaticSiteFixture site = new(Q());
        StaticSiteFixture.Tab tab = site.NewTab();
        site.Handler.Respond = _ => throw new HttpRequestException("offline");
        tab.Hook.ReadGate = _ => throw new InvalidOperationException("storage blew up");
        List<Exception> unobserved = [];
        void Collect(object? sender, UnobservedTaskExceptionEventArgs e) => unobserved.AddRange(e.Exception.InnerExceptions);
        TaskScheduler.UnobservedTaskException += Collect;
        try
        {
            ApiResult<SessionPlan> practice = await tab.Backend.PracticeAsync(Bootstrap(site), Practice(), Ct);
            ApiResult<SessionPlan> review = await tab.Backend.ReviewAsync(Bootstrap(site), new ReviewSessionRequest("en", "dotnet", 20), Ct);
            ApiResult<ProfileDto> profile = await tab.Backend.ProfileAsync(Bootstrap(site), Ct);
            Assert.Equal(0, practice.Status);
            Assert.Equal(0, review.Status);
            Assert.Equal(0, profile.Status);

            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(20, Ct);
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Collect;
        }

        Assert.Empty(unobserved);
    }
}
