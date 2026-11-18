using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Trainer.Contracts;
using Trainer.Web.Client.Api;
using Trainer.Web.Client.Backend;
using Trainer.Web.Client.Islands;
using Trainer.Web.Client.Storage;

namespace Trainer.Web.Client.Tests;

/// <summary>Behaviour of the islands themselves, driven through their rendered buttons (see <see cref="IslandHarness"/>).</summary>
public sealed class IslandBehaviourTests
{
    private static readonly ApiResult<QuestionDto> Offline = new(null, 0, null);

    [Fact]
    public async Task Practice_TryAgain_AfterAFailedQuestionLoad_RepeatsThatLoadNotTheSessionRequest()
    {
        QuestionDto first = TestQuestions.Open();
        QuestionDto second = TestQuestions.Open();
        RecordingQuestionSource source = new([first, second]);
        source.Hold(second.Id).SetResult(Offline);
        ScriptedBackend backend = new() { Practice = () => new ApiResult<SessionPlan>(source.Plan(), 200, null) };
        await using IslandHarness harness = new(backend);
        await harness.RenderAsync<PracticeIsland>(IslandHarness.BootstrapJson());
        await harness.ClickAsync("Start");

        await await harness.SkipToNextAsync();

        Assert.Contains("You are offline", await harness.TextAsync(), StringComparison.Ordinal);
        await harness.ClickAsync("Try again");

        Assert.Equal(1, backend.PracticeCalls);
        Assert.Equal(2, source.Calls.Count(c => c == $"get:{second.Id}"));
        Assert.Contains("Question 2 of 2", await harness.TextAsync(), StringComparison.Ordinal);
        Assert.Empty(harness.Errors);
    }

    [Fact]
    public async Task Practice_TryAgain_AfterAFailedSessionRequest_RepeatsTheRequest()
    {
        QuestionDto only = TestQuestions.Open();
        RecordingQuestionSource source = new([only]);
        ScriptedBackend backend = new();
        await using IslandHarness harness = new(backend);
        await harness.RenderAsync<PracticeIsland>(IslandHarness.BootstrapJson());
        await harness.ClickAsync("Start");
        Assert.Contains("You are offline", await harness.TextAsync(), StringComparison.Ordinal);

        backend.Practice = () => new ApiResult<SessionPlan>(source.Plan(), 200, null);
        await harness.ClickAsync("Try again");

        Assert.Equal(2, backend.PracticeCalls);
        Assert.Contains("Question 1 of 1", await harness.TextAsync(), StringComparison.Ordinal);
        Assert.Empty(harness.Errors);
    }

    [Fact]
    public async Task Practice_DisposedWhileAQuestionLoads_SurfacesNoException()
    {
        QuestionDto first = TestQuestions.Open();
        QuestionDto second = TestQuestions.Open();
        RecordingQuestionSource source = new([first, second]);
        TaskCompletionSource<ApiResult<QuestionDto>> gate = source.Hold(second.Id);
        ScriptedBackend backend = new() { Practice = () => new ApiResult<SessionPlan>(source.Plan(), 200, null) };
        await using IslandHarness harness = new(backend);
        await harness.RenderAsync<PracticeIsland>(IslandHarness.BootstrapJson());
        await harness.ClickAsync("Start");
        Task next = await harness.SkipToNextAsync();
        Assert.False(next.IsCompleted);
        Assert.Contains("Loading the next question", await harness.TextAsync(), StringComparison.Ordinal);

        await harness.DisposeIslandAsync();
        gate.SetException(new OperationCanceledException());
        await next;

        Assert.Empty(harness.Errors);
    }

    [Theory]
    [InlineData(true, "Question 1 of 1")]
    [InlineData(false, "Question 1 of 2")]
    public async Task Review_DropsQuestionsWithPendingAnswers_OnlyWhenTheBackendFiltersThem(bool filters, string position)
    {
        QuestionDto answered = TestQuestions.Open();
        QuestionDto due = TestQuestions.Open();
        RecordingQuestionSource source = new([answered, due]);
        ScriptedBackend backend = new()
        {
            Progress = true,
            FiltersPendingAnswers = filters,
            Review = () => new ApiResult<SessionPlan>(source.Plan(), 200, null),
        };
        await using IslandHarness harness = new(backend);
        await harness.Outbox.AddAsync(
            backend.Key,
            new AnswerDto("01JC0000000000000000000001", answered.Id, "knew", false, "review", null, new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero)));

        await harness.RenderAsync<ReviewIsland>(IslandHarness.BootstrapJson());

        Assert.Equal(1, backend.ReviewCalls);
        Assert.Contains(position, await harness.TextAsync(), StringComparison.Ordinal);
        // The filtered queue starts with the question that is still due, not the one already answered.
        Assert.Equal(filters ? [$"get:{due.Id}"] : [$"get:{answered.Id}", $"prefetch:{due.Id}"], source.Calls);
        Assert.Empty(harness.Errors);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IslandHarness StaticHarness(StaticSiteFixture site, HookStore store, string page = "http://localhost/en/dotnet/practice") =>
        new(services => site.Configure(services, store), page);

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        for (int i = 0; i < 1000; i++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(10, Ct);
        }

        Assert.Fail("The condition did not become true in time.");
    }

    [Fact]
    public async Task Mock_Static_AFinishedMockIsKeptInTheLocalHistory()
    {
        QuestionDto question = TestQuestions.Single();
        StaticSiteFixture site = new(question);
        HookStore store = new(site.Store);
        await using IslandHarness harness = StaticHarness(site, store, "http://localhost/en/dotnet/mock");
        await harness.RenderAsync<MockIsland>(StaticSiteFixture.BootstrapJson());

        await harness.ClickAsync("Start the interview");
        // One question in the pool is warned about before it begins; the visitor goes on.
        await harness.ClickAsync("Start anyway");
        await harness.ClickAsync("Finish the interview");

        MockHistoryDto saved = Assert.Single(await site.NewTab().History.ReadAsync("dotnet"));
        Assert.Equal("middle", saved.Level);
        Assert.Equal(1, saved.QuestionCount);
        Assert.Equal(1, saved.Unanswered);
        Assert.Empty(harness.Errors);
    }

    // The harness is built by the caller: it sets the culture, which only reaches the test's awaits from synchronous code.
    private static async Task StartMockAsync(IslandHarness harness)
    {
        await harness.RenderAsync<MockIsland>(StaticSiteFixture.BootstrapJson());
        await harness.ClickAsync("Start the interview");
        await harness.ClickAsync("Start anyway");
    }

    private static ValueTask<StoreResult?> NoIntercept() => ValueTask.FromResult<StoreResult?>(null);

    [Fact]
    public async Task Mock_Static_TheHistoryIsWrittenBeforeTheMockIsCleared()
    {
        QuestionDto question = TestQuestions.Single();
        StaticSiteFixture site = new(question);
        HookStore store = new(site.Store);
        await using IslandHarness harness = StaticHarness(site, store, "http://localhost/en/dotnet/mock");
        await StartMockAsync(harness);

        await harness.ClickAsync("Finish the interview");

        int recorded = site.Store.Ops.FindIndex(o => o.Op == "historyAdd");
        int cleared = site.Store.Ops.FindIndex(o => o.Op == "delete");
        Assert.True(recorded >= 0 && cleared >= 0, "both ops expected");
        Assert.True(recorded < cleared, "the history must exist before the mock's keys are removed");
        Assert.Null(site.Store.Raw(StorageKeys.MockProgress("anon")));
    }

    [Fact]
    public async Task Mock_Static_AFailingHistory_NeverStopsTheSummaryOrTheClear()
    {
        QuestionDto question = TestQuestions.Single();
        StaticSiteFixture site = new(question);
        HookStore store = new(site.Store)
        {
            Intercept = op => op.Op == "historyAdd" ? throw new InvalidOperationException("boom") : NoIntercept(),
        };
        await using IslandHarness harness = StaticHarness(site, store, "http://localhost/en/dotnet/mock");
        await StartMockAsync(harness);

        await harness.ClickAsync("Finish the interview");

        Assert.Contains("Interview results", await harness.TextAsync(), StringComparison.Ordinal);
        Assert.Null(site.Store.Raw(StorageKeys.MockProgress("anon")));
        Assert.Empty(harness.Errors);
    }

    [Fact]
    public async Task Mock_Static_AnswersThatDidNotReachStorage_AreNotRecordedInTheHistory()
    {
        QuestionDto question = TestQuestions.Open();
        StaticSiteFixture site = new(question);
        HookStore store = new(site.Store)
        {
            Intercept = op => ValueTask.FromResult<StoreResult?>(op.Op == "append" ? StoreResult.Failed(StoreErrors.Quota) : null),
        };
        await using IslandHarness harness = StaticHarness(site, store, "http://localhost/en/dotnet/mock");
        await StartMockAsync(harness);
        await harness.ClickAsync("Finish the interview");
        await harness.ClickAsync("Knew it");

        Assert.Contains("Interview results", await harness.TextAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(site.Store.Ops, o => o.Op == "historyAdd");
        Assert.Empty(await site.NewTab().History.ReadAsync("dotnet"));
        Assert.Empty(harness.Errors);
    }

    [Fact]
    public async Task Usable_WaitsForIndexAndProgress()
    {
        StaticSiteFixture site = new(TestQuestions.Open(), TestQuestions.Open());
        HookStore store = new(site.Store);
        TaskCompletionSource indexGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource progressGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        site.Handler.Gate = request => request.Path.Contains("/index.", StringComparison.Ordinal) ? indexGate.Task : Task.CompletedTask;
        store.ReadGate = key => string.Equals(key, StorageKeys.AnonymousOutbox, StringComparison.Ordinal) ? progressGate.Task : Task.CompletedTask;
        await using IslandHarness harness = StaticHarness(site, store);

        Task started = await harness.StartRenderAsync<PracticeIsland>(StaticSiteFixture.BootstrapJson());
        Assert.Null(await harness.AttributeAsync("data-island-ready"));

        // The index alone is not enough.
        indexGate.SetResult();
        await Task.Delay(100, Ct);
        Assert.False(started.IsCompleted);
        Assert.Null(await harness.AttributeAsync("data-island-ready"));

        progressGate.SetResult();
        await started.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal("true", await harness.AttributeAsync("data-island-ready"));
        Assert.True(await harness.HasButtonAsync("Start"));
        Assert.Empty(harness.Errors);
    }

    [Fact]
    public async Task Usable_AlsoWaitsForTheIndexWhenProgressIsReadyFirst()
    {
        StaticSiteFixture site = new(TestQuestions.Open());
        HookStore store = new(site.Store);
        TaskCompletionSource indexGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        site.Handler.Gate = request => request.Path.Contains("/index.", StringComparison.Ordinal) ? indexGate.Task : Task.CompletedTask;
        await using IslandHarness harness = StaticHarness(site, store);

        Task started = await harness.StartRenderAsync<PracticeIsland>(StaticSiteFixture.BootstrapJson());
        await Task.Delay(100, Ct);

        Assert.False(started.IsCompleted);
        Assert.Null(await harness.AttributeAsync("data-island-ready"));

        indexGate.SetResult();
        await started.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal("true", await harness.AttributeAsync("data-island-ready"));
    }

    [Fact]
    public async Task Usable_WaitsForTheFirstQuestionOfAFirstLink()
    {
        // Until the question is on screen the island shows a skeleton, which is nothing to act on.
        QuestionDto[] questions = [TestQuestions.Open(), TestQuestions.Open()];
        StaticSiteFixture site = new(questions);
        TaskCompletionSource questionGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        site.Handler.Gate = request => request.Path.Contains("/q/", StringComparison.Ordinal) ? questionGate.Task : Task.CompletedTask;
        await using IslandHarness harness = StaticHarness(site, new HookStore(site.Store), $"http://localhost/en/dotnet/practice?first={questions[0].Id}");

        Task started = await harness.StartRenderAsync<PracticeIsland>(StaticSiteFixture.BootstrapJson());
        await Task.Delay(200, Ct);

        Assert.False(started.IsCompleted);
        Assert.Null(await harness.AttributeAsync("data-island-ready"));
        Assert.DoesNotContain(questions[0].Title, await harness.TextAsync(), StringComparison.Ordinal);

        questionGate.SetResult();
        await started.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal("true", await harness.AttributeAsync("data-island-ready"));
        Assert.Contains(questions[0].Title, await harness.TextAsync(), StringComparison.Ordinal);
        Assert.Empty(harness.Errors);
    }

    [Fact]
    public async Task Usable_WaitsForTheFirstQuestionOfAReview()
    {
        // The review's first question is fetched after the queue is picked; until it is on screen the island shows a skeleton,
        // which is nothing to act on, so it is not usable (an empty shell must not pass the usable mark).
        QuestionDto due = TestQuestions.Open();
        StaticSiteFixture site = new(due, TestQuestions.Open());
        HookStore store = new(site.Store);
        TaskCompletionSource questionGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        site.Handler.Gate = request => request.Path.Contains("/q/", StringComparison.Ordinal) ? questionGate.Task : Task.CompletedTask;
        await using IslandHarness harness = StaticHarness(site, store, "http://localhost/en/dotnet/review");
        await harness.Outbox.AddAsync(StorageKeys.AnonymousOutbox, StaticSiteFixture.Answer(due, "knew", StaticSiteFixture.Start.AddDays(-3)));

        Task started = await harness.StartRenderAsync<ReviewIsland>(StaticSiteFixture.BootstrapJson());
        await Task.Delay(200, Ct);

        Assert.False(started.IsCompleted);
        Assert.Null(await harness.AttributeAsync("data-island-ready"));
        Assert.DoesNotContain(due.Title, await harness.TextAsync(), StringComparison.Ordinal);

        questionGate.SetResult();
        await started.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal("true", await harness.AttributeAsync("data-island-ready"));
        Assert.Contains(due.Title, await harness.TextAsync(), StringComparison.Ordinal);
        Assert.Empty(harness.Errors);
    }

    [Fact]
    public async Task IslandRoot_RaisesIslandUsableOnce()
    {
        StaticSiteFixture site = new(TestQuestions.Open());
        HookStore store = new(site.Store);
        TaskCompletionSource indexGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        site.Handler.Gate = request => request.Path.Contains("/index.", StringComparison.Ordinal) ? indexGate.Task : Task.CompletedTask;
        await using IslandHarness harness = StaticHarness(site, store);
        FakeJsRuntime js = (FakeJsRuntime)harness.Services.GetRequiredService<IJSRuntime>();

        Task started = await harness.StartRenderAsync<PracticeIsland>(StaticSiteFixture.BootstrapJson());
        await Task.Delay(100, Ct);
        // Still loading: nothing to act on, so nothing is announced.
        Assert.DoesNotContain(js.Calls, c => c.Identifier == "ditPageLifecycle.announceUsable");

        indexGate.SetResult();
        await started.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await UntilAsync(() => Task.FromResult(js.Calls.Any(c => c.Identifier == "ditPageLifecycle.announceUsable")));
        // Later renders of the same island do not announce again.
        await harness.ClickAsync("Start");

        Assert.Single(js.Calls, c => c.Identifier == "ditPageLifecycle.announceUsable");
        Assert.Empty(harness.Errors);
    }

    [Fact]
    public async Task FirstLink_ShowsTheQuestionWithoutWaitingForProgress_NextWaitsForSelection()
    {
        QuestionDto[] questions = [TestQuestions.Open(), TestQuestions.Open(), TestQuestions.Open()];
        StaticSiteFixture site = new(questions);
        HookStore store = new(site.Store);
        TaskCompletionSource progressGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        store.ReadGate = key => string.Equals(key, StorageKeys.AnonymousOutbox, StringComparison.Ordinal) ? progressGate.Task : Task.CompletedTask;
        await using IslandHarness harness = StaticHarness(site, store, $"http://localhost/en/dotnet/practice?first={questions[1].Id}");

        // Start-up finishes with the progress replay still held back.
        await harness.RenderAsync<PracticeIsland>(StaticSiteFixture.BootstrapJson());

        string text = await harness.TextAsync();
        Assert.Contains(questions[1].Title, text, StringComparison.Ordinal);
        Assert.Equal("true", await harness.AttributeAsync("data-island-ready"));
        // The size is not known yet, so no position is shown rather than a guess.
        Assert.DoesNotContain("Question 1 of", text, StringComparison.Ordinal);
        await harness.ClickAsync("Show reference answer");
        await harness.ClickAsync("Skip");
        Assert.True(await harness.IsDisabledAsync("Next question"));

        // A press on the disabled button (a keyboard shortcut reaches the same handler) changes nothing.
        Task pressed = await harness.StartClickAsync("Next question");
        await pressed.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Contains(questions[1].Title, await harness.TextAsync(), StringComparison.Ordinal);
        Assert.Single(site.QuestionRequests);

        progressGate.SetResult();
        await UntilAsync(async () => !await harness.IsDisabledAsync("Next question"));
        Assert.Contains("Question 1 of 3", await harness.TextAsync(), StringComparison.Ordinal);
        Assert.Equal(2, site.QuestionRequests.Count);

        await harness.ClickAsync("Next question");
        Assert.Contains("Question 2 of 3", await harness.TextAsync(), StringComparison.Ordinal);
        Assert.Empty(harness.Errors);
    }

    [Fact]
    public async Task FirstLink_FocusWaitsOnAnEnabledControl_AndMovesToNextWhenItEnables()
    {
        QuestionDto[] questions = [TestQuestions.Open(), TestQuestions.Open(), TestQuestions.Open()];
        StaticSiteFixture site = new(questions);
        HookStore store = new(site.Store);
        TaskCompletionSource progressGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        store.ReadGate = key => string.Equals(key, StorageKeys.AnonymousOutbox, StringComparison.Ordinal) ? progressGate.Task : Task.CompletedTask;
        await using IslandHarness harness = StaticHarness(site, store, $"http://localhost/en/dotnet/practice?first={questions[1].Id}");
        FakeJsRuntime js = (FakeJsRuntime)harness.Services.GetRequiredService<IJSRuntime>();
        await harness.RenderAsync<PracticeIsland>(StaticSiteFixture.BootstrapJson());
        await harness.ClickAsync("Show reference answer");
        int before = FocusCalls(js).Count;
        await harness.ClickAsync("Skip");
        Assert.True(await harness.IsDisabledAsync("Next question"));

        // focus() on a disabled button does nothing and focus would fall to the body: it waits on an enabled control.
        List<string?> pending = FocusCalls(js);
        Assert.Equal(before + 1, pending.Count);

        progressGate.SetResult();
        await UntilAsync(async () => !await harness.IsDisabledAsync("Next question"));
        await UntilAsync(() => Task.FromResult(FocusCalls(js).Count == before + 2));

        List<string?> all = FocusCalls(js);
        Assert.NotEqual(all[^1], all[^2]);
        Assert.Empty(harness.Errors);
    }

    private static List<string?> FocusCalls(FakeJsRuntime js) =>
        [.. js.Calls
            .Where(c => c.Identifier.EndsWith(".focus", StringComparison.Ordinal))
            .Select(c => c.Args?.OfType<Microsoft.AspNetCore.Components.ElementReference>().FirstOrDefault().Id)];

    [Fact]
    public async Task Review_WithTheStaticBackend_OffersADueQuestionWhoseAnswerIsStillInTheOutbox()
    {
        QuestionDto answered = TestQuestions.Open();
        QuestionDto other = TestQuestions.Open();
        StaticSiteFixture site = new(answered, other);
        HookStore store = new(site.Store);
        await using IslandHarness harness = StaticHarness(site, store, "http://localhost/en/dotnet/review");
        // Answered three days ago, so due again; the answer was never sent anywhere and never leaves the outbox.
        await harness.Outbox.AddAsync(
            StorageKeys.AnonymousOutbox,
            StaticSiteFixture.Answer(answered, "knew", StaticSiteFixture.Start.AddDays(-3)));

        Assert.False(harness.Backend.FiltersPendingAnswers);
        await harness.RenderAsync<ReviewIsland>(StaticSiteFixture.BootstrapJson());

        Assert.Contains("Question 1 of 1", await harness.TextAsync(), StringComparison.Ordinal);
        Assert.Equal([StaticSiteFixture.QuestionPath(answered)], site.QuestionRequests);
        Assert.Empty(harness.Errors);
    }

    [Fact]
    public async Task Practice_WithTheStaticBackend_AnswersLandInTheAnonymousOutboxAndShowTheReportLink()
    {
        QuestionDto only = TestQuestions.Open();
        StaticSiteFixture site = new(only);
        HookStore store = new(site.Store);
        await using IslandHarness harness = StaticHarness(site, store);
        await harness.RenderAsync<PracticeIsland>(StaticSiteFixture.BootstrapJson());
        await harness.ClickAsync("Start");

        await harness.ClickAsync("Show reference answer");
        await harness.ClickAsync("Knew it");

        string text = await harness.TextAsync();
        Assert.Contains("Report a problem", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Like", text, StringComparison.Ordinal);
        AnswerDto saved = Assert.Single(await harness.Outbox.ReadAsync(StorageKeys.AnonymousOutbox));
        Assert.Equal(only.Id, saved.QuestionId);
        Assert.Equal("knew", saved.Grade);
        Assert.Equal(StaticSiteFixture.Start, saved.AnsweredAt);
        Assert.DoesNotContain(site.Handler.Requests, r => r.Path.StartsWith("/api", StringComparison.Ordinal));
        Assert.Empty(harness.Errors);
    }
}
