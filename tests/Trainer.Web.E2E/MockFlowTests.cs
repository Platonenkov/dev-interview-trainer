using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Trainer.Infrastructure.Persistence;
using Trainer.TestSupport;

namespace Trainer.Web.E2E;

/// <summary>
/// The Mock interview in a real browser: setup and the small-pool warning, answering without feedback, the timer, the
/// assessment and the summary, resuming after a reload, a second tab, the hand-over to the outbox and the server, a
/// storage lock that never comes, and the layout. Every test checks that no document it showed reported a CSP violation.
/// </summary>
[Trait("Category", "E2E")]
public sealed class MockFlowTests(AppHostFixture host) : IClassFixture<AppHostFixture>
{
    private const string MockPath = "/en/dotnet/mock";
    private const string ReadyIsland = ".island-root[data-island-ready='true']";
    private const string AnonymousProgress = "dit.mock.v1.anon.active";
    private const string AnonymousOutbox = "dit.outbox.v1.anon";
    private const int MinimumTarget = 44;

    private static readonly TimeSpan IslandTimeout = TimeSpan.FromSeconds(60);
    private static readonly string ScreenshotDirectory = Path.Combine(ContentWorkspace.RepositoryRoot(), "TestResults", "screenshots");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static float Wait => (float)IslandTimeout.TotalMilliseconds;

    private async Task<IBrowserContext> NewContextAsync(
        int width = 1280, int height = 900, string theme = "dark", bool touch = false, bool mobile = false)
    {
        IBrowserContext context = await host.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString(),
            Locale = "en-US",
            ViewportSize = new ViewportSize { Width = width, Height = height },
            HasTouch = touch,
            IsMobile = mobile,
        });
        await context.AddInitScriptAsync($"try {{ localStorage.setItem('theme', '{theme}'); }} catch (e) {{ }}");
        return context;
    }

    private async Task<IPage> OpenAsync(IBrowserContext context, string query = "", Func<IPage, Task>? beforeNavigation = null)
    {
        IPage tab = await host.NewPageAsync(context);
        if (beforeNavigation is not null)
        {
            await beforeNavigation(tab);
        }

        await tab.GotoAsync(MockPath + query);
        await tab.Locator(ReadyIsland).WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        return tab;
    }

    private static async Task ChooseAsync(IPage tab, string level = "middle", int count = 5, bool timed = false)
    {
        await tab.Locator(".mock-setup").WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        await tab.Locator($".mock-setup input[name='mock-level'][value='{level}']").CheckAsync();
        ILocator countInput = tab.Locator(".mock-setup input[name='count']");
        await countInput.FillAsync(count.ToString(CultureInfo.InvariantCulture));
        await countInput.DispatchEventAsync("change");
        if (timed)
        {
            await tab.Locator(".mock-setup input[name='timer']").CheckAsync();
        }
    }

    private static Task WaitForPhaseAsync(IPage tab, params string[] phases) =>
        tab.Locator(string.Join(", ", phases.Select(p => $".mock[data-mock-phase='{p}']")))
            .First.WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });

    private static async Task<string> PhaseAsync(IPage tab) => await tab.Locator(".mock").GetAttributeAsync("data-mock-phase") ?? "";

    /// <summary>Chooses, starts and, past a small-pool warning, starts anyway: ends in the answering phase.</summary>
    private static async Task StartAsync(IPage tab, string level = "middle", int count = 5, bool timed = false)
    {
        await ChooseAsync(tab, level, count, timed);
        await tab.Locator(".mock-setup__start").ClickAsync();
        await WaitForPhaseAsync(tab, "prepared", "answering");
        if (await PhaseAsync(tab) == "prepared")
        {
            await tab.Locator(".mock-warn__anyway").ClickAsync();
        }

        await WaitForPhaseAsync(tab, "answering");
    }

    private static async Task<long> RevisionAsync(IPage tab, string key) =>
        await tab.EvaluateAsync<long>("k => { const v = localStorage.getItem(k); return v ? JSON.parse(v).revision : 0; }", key);

    private static async Task WaitForRevisionAsync(IPage tab, string key, long above) =>
        await tab.WaitForFunctionAsync(
            "([k, r]) => { const v = localStorage.getItem(k); return v !== null && JSON.parse(v).revision > r; }",
            new object[] { key, above },
            new PageWaitForFunctionOptions { Timeout = Wait });

    // Gives the question on screen a response (an option, a line, typed text or notes) and waits until it is saved.
    private static async Task AnswerCurrentAsync(IPage tab, string progressKey)
    {
        long before = await RevisionAsync(tab, progressKey);
        await tab.Locator(".qcard").WaitForAsync();
        if (await tab.Locator(".opt--pick").CountAsync() > 0)
        {
            await tab.Locator(".opt--pick").First.ClickAsync();
        }
        else if (await tab.Locator(".code-line").CountAsync() > 0)
        {
            await tab.Locator(".code-line").First.ClickAsync();
        }
        else if (await tab.Locator(".typed__input").CountAsync() > 0)
        {
            await tab.Locator(".typed__input").FillAsync("x");
        }
        else
        {
            await tab.Locator(".self__notes").FillAsync("my notes");
        }

        await WaitForRevisionAsync(tab, progressKey, before);
    }

    private static async Task AnswerAllAsync(IPage tab, string progressKey)
    {
        int count = await tab.Locator(".jump__btn").CountAsync();
        for (int i = 0; i < count; i++)
        {
            await tab.Locator(".jump__btn").Nth(i).ClickAsync();
            await Assertions.Expect(tab.Locator(".jump__btn").Nth(i)).ToHaveAttributeAsync("aria-current", "step");
            await AnswerCurrentAsync(tab, progressKey);
        }
    }

    private static async Task FinishAsync(IPage tab)
    {
        await tab.Locator(".mock__finish").ClickAsync();
        await WaitForPhaseAsync(tab, "assessment", "summary");
    }

    // Grades every open item of the assessment with the given grade class (knew, partial, didnt-know) or skips them.
    private static async Task AssessAllAsync(IPage tab, string? grade)
    {
        while (await PhaseAsync(tab) == "assessment")
        {
            ILocator open = tab.Locator(".mock-assess__item:has(.self__grades)").First;
            await open.Locator(grade is null ? ".self__grades .btn--quiet" : $".self__grade--{grade}").ClickAsync();
            await WaitForPhaseAsync(tab, "assessment", "summary");
            await tab.WaitForFunctionAsync(
                "() => !document.querySelector('.mock-assess [role=status]')",
                null,
                new PageWaitForFunctionOptions { Timeout = Wait });
        }

        await WaitForPhaseAsync(tab, "summary");
    }

    private static async Task<JsonElement?> StoredAsync(IPage tab, string key)
    {
        string? raw = await tab.EvaluateAsync<string?>("k => localStorage.getItem(k)", key);
        return raw is null ? null : JsonDocument.Parse(raw).RootElement.Clone();
    }

    private static async Task<int> StoredCountAsync(IPage tab, string key) =>
        await StoredAsync(tab, key) is { } document ? document.GetProperty("answers").GetArrayLength() : 0;

    private async Task<int> ServerCountAsync(string table, long telegramUserId)
    {
        await using TrainerDbContext db = PostgresFixture.CreateContext(host.ConnectionString);
        return table switch
        {
            "mock_sessions" => await db.Database
                .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM mock_sessions m JOIN users u ON u.id = m.user_id WHERE u.telegram_user_id = {telegramUserId}")
                .SingleAsync(Ct),
            _ => await db.Database
                .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM answers a JOIN users u ON u.id = a.user_id WHERE u.telegram_user_id = {telegramUserId} AND a.mock_session_id IS NOT NULL")
                .SingleAsync(Ct),
        };
    }

    private async Task WaitForServerCountAsync(string table, long telegramUserId, int expected, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        int last;
        while ((last = await ServerCountAsync(table, telegramUserId)) != expected)
        {
            Assert.True(DateTime.UtcNow < deadline, $"Expected {expected} {table} rows for {telegramUserId}, found {last}.");
            await Task.Delay(250, Ct);
        }
    }

    private static string Resource(string key)
    {
        string path = Path.Combine(ContentWorkspace.RepositoryRoot(), "src", "Trainer.Web.Client", "Localization", "ClientResource.en.resx");
        return XDocument.Load(path).Descendants("data").Single(d => (string?)d.Attribute("name") == key).Element("value")!.Value;
    }

    // Records the request bodies of /api/sessions/mock.
    private static async Task<List<string>> TapRequestsAsync(IPage tab)
    {
        List<string> bodies = [];
        await tab.RouteAsync("**/api/sessions/mock", async route =>
        {
            lock (bodies)
            {
                bodies.Add(route.Request.PostData ?? "");
            }

            await route.ContinueAsync();
        });
        return bodies;
    }

    [Fact]
    public async Task Mock_UntimedFiveQuestions_AssessmentThenSummary()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await OpenAsync(context);

        await StartAsync(tab);

        Assert.Equal(5, await tab.Locator(".jump__btn").CountAsync());
        Assert.Equal(0, await tab.Locator(".mock-timer").CountAsync());
        await AnswerAllAsync(tab, AnonymousProgress);
        // No feedback of any kind while answering.
        Assert.Equal(0, await tab.Locator(".feedback, .opt--good, .opt--bad, .opt__verdict, .self__reference").CountAsync());
        Assert.Equal(5, await tab.Locator(".jump__btn--answered").CountAsync());
        Assert.Equal(0, await StoredCountAsync(tab, AnonymousOutbox));

        await FinishAsync(tab);
        if (await PhaseAsync(tab) == "assessment")
        {
            await Assertions.Expect(tab.Locator(".mock-assess__item .self__reference").First).ToBeVisibleAsync();
            Assert.Equal("my notes", await tab.Locator(".mock-assess__item .self__notes").First.InputValueAsync());
        }

        await AssessAllAsync(tab, "knew");

        ILocator items = tab.Locator(".mock-summary__list li");
        Assert.Equal(5, await items.CountAsync());
        for (int i = 0; i < 5; i++)
        {
            string href = await items.Nth(i).Locator("a").GetAttributeAsync("href") ?? "";
            Assert.StartsWith("/en/dotnet/questions/", href, StringComparison.Ordinal);
        }

        await Assertions.Expect(tab.Locator(".mock-summary__table tbody tr").First).ToBeVisibleAsync();
        await Assertions.Expect(tab.Locator("#mock-summary-heading")).ToBeFocusedAsync();
        Assert.Equal(5, await StoredCountAsync(tab, AnonymousOutbox));
        Assert.Null(await StoredAsync(tab, AnonymousProgress));
        Assert.True(await tab.EvaluateAsync<bool>("() => Object.keys(localStorage).every(k => !k.startsWith('dit.mock.v1.anon.'))"));
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Mock_TimerExpires_GoesToAssessment()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await OpenAsync(context, "?topic=async", t => t.Clock.InstallAsync());

        await StartAsync(tab, timed: true);
        await Assertions.Expect(tab.Locator(".mock-timer__value")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex("^7:(30|2[0-9])$"));
        Assert.Equal("timer", await tab.Locator(".mock-timer__value").GetAttributeAsync("role"));
        await AnswerCurrentAsync(tab, AnonymousProgress);

        await tab.Clock.FastForwardAsync(451_000);

        await WaitForPhaseAsync(tab, "assessment");
        Assert.True(await tab.Locator(".mock-assess__item").CountAsync() >= 3);
        await AssessAllAsync(tab, null);
        await Assertions.Expect(tab.Locator(".mock-summary")).ToBeVisibleAsync();
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Mock_SmallPool_WarnsBeforeAnswering()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await OpenAsync(context);
        await ChooseAsync(tab);

        await tab.Locator(".mock-setup__start").ClickAsync();

        await WaitForPhaseAsync(tab, "prepared");
        await Assertions.Expect(tab.Locator(".mock-warn")).ToContainTextAsync("sessions will repeat questions");
        Assert.Equal(0, await tab.Locator(".qcard").CountAsync());
        // The suggestion equals the count asked for, so it is not offered (a third of a pool of 8 is below the minimum 5).
        Assert.Equal(0, await tab.Locator(".mock-warn__use").CountAsync());
        Assert.Null(await StoredAsync(tab, AnonymousProgress));

        await tab.Locator(".mock-warn__anyway").ClickAsync();

        await WaitForPhaseAsync(tab, "answering");
        await Assertions.Expect(tab.Locator(".qcard")).ToBeVisibleAsync();
        Assert.NotNull(await StoredAsync(tab, AnonymousProgress));
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Mock_ChangingFilterInPrepared_DiscardsQuestions()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await OpenAsync(context);
        await ChooseAsync(tab);
        await tab.Locator(".mock-setup__start").ClickAsync();
        await WaitForPhaseAsync(tab, "prepared");

        await tab.Locator(".mock-setup input[name='mock-level'][value='senior']").CheckAsync();

        await WaitForPhaseAsync(tab, "setup");
        Assert.Equal(0, await tab.Locator(".mock-warn").CountAsync());
        await Assertions.Expect(tab.Locator(".mock-setup__start")).ToBeVisibleAsync();
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Mock_UseSuggestedCount_RefetchesWithSuggestion()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await host.NewPageAsync(context);
        List<string> requests = await TapRequestsAsync(tab);
        await tab.GotoAsync(MockPath);
        await tab.Locator(ReadyIsland).WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        await ChooseAsync(tab, count: 20);
        await tab.Locator(".mock-setup__start").ClickAsync();
        await WaitForPhaseAsync(tab, "prepared");

        await Assertions.Expect(tab.Locator(".mock-warn__use")).ToHaveTextAsync("Use N = 5");
        await Assertions.Expect(tab.Locator(".mock-warn")).ToContainTextAsync("Try N = 5.");
        await tab.Locator(".mock-warn__use").ClickAsync();

        await tab.WaitForFunctionAsync("() => document.querySelector('.mock[data-mock-phase=prepared] .mock-warn__anyway') !== null");
        Assert.Equal(2, requests.Count);
        using JsonDocument first = JsonDocument.Parse(requests[0]);
        using JsonDocument second = JsonDocument.Parse(requests[1]);
        Assert.Equal(20, first.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(5, second.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("5", await tab.Locator(".mock-setup input[name='count']").InputValueAsync());
        Assert.Equal(0, await tab.Locator(".mock-warn__use").CountAsync());
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Mock_EmptyPool_ShowsEmptyState()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await OpenAsync(context);
        await ChooseAsync(tab, level: "architect");

        await tab.Locator(".mock-setup__start").ClickAsync();

        await Assertions.Expect(tab.Locator(".island-state[role='status']")).ToContainTextAsync(Resource("State.Empty"));
        Assert.Equal(0, await tab.Locator(".qcard").CountAsync());
        await tab.Locator(".island-state .btn").ClickAsync();
        await WaitForPhaseAsync(tab, "setup");
        Assert.True(await tab.Locator(".mock-setup input[name='mock-level'][value='architect']").IsCheckedAsync());
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Mock_ReloadMidSession_Resumes()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await OpenAsync(context);
        await StartAsync(tab, timed: true);
        await AnswerCurrentAsync(tab, AnonymousProgress);
        string firstTitle = await tab.Locator(".qcard__title").InnerTextAsync();
        long before = await RevisionAsync(tab, AnonymousProgress);
        await tab.Locator(".mock__next").ClickAsync();
        await WaitForRevisionAsync(tab, AnonymousProgress, before);
        string secondTitle = await tab.Locator(".qcard__title").InnerTextAsync();
        JsonElement saved = (await StoredAsync(tab, AnonymousProgress))!.Value;

        await tab.ReloadAsync();
        await tab.Locator(ReadyIsland).WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });

        await WaitForPhaseAsync(tab, "answering");
        await Assertions.Expect(tab.Locator(".qcard__title")).ToHaveTextAsync(secondTitle);
        await Assertions.Expect(tab.Locator(".jump__btn").Nth(1)).ToHaveAttributeAsync("aria-current", "step");
        await Assertions.Expect(tab.Locator(".jump__btn").Nth(0)).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("jump__btn--answered"));
        await Assertions.Expect(tab.Locator(".mock-timer")).ToBeVisibleAsync();
        JsonElement after = (await StoredAsync(tab, AnonymousProgress))!.Value;
        Assert.Equal(saved.GetProperty("sessionId").GetString(), after.GetProperty("sessionId").GetString());
        Assert.Equal(saved.GetProperty("deadline").GetString(), after.GetProperty("deadline").GetString());
        await tab.Locator(".jump__btn").Nth(0).ClickAsync();
        await Assertions.Expect(tab.Locator(".qcard__title")).ToHaveTextAsync(firstTitle);
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Mock_ReloadDuringAssessment_KeepsGrades()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await OpenAsync(context, "?topic=async");
        await StartAsync(tab);
        await FinishAsync(tab);
        await WaitForPhaseAsync(tab, "assessment");
        int items = await tab.Locator(".mock-assess__item").CountAsync();
        Assert.True(items >= 3, $"Expected at least 3 open questions, found {items}.");

        ILocator first = tab.Locator(".mock-assess__item").First;
        await first.Locator(".self__grade--partial").ClickAsync();
        await Assertions.Expect(first.Locator(".mock-assess__result")).ToHaveTextAsync("Your grade: Partially");
        string title = await first.Locator(".qcard__title").InnerTextAsync();

        await tab.ReloadAsync();
        await tab.Locator(ReadyIsland).WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });

        await WaitForPhaseAsync(tab, "assessment");
        ILocator restored = tab.Locator(".mock-assess__item").First;
        await Assertions.Expect(restored.Locator(".qcard__title")).ToHaveTextAsync(title);
        await Assertions.Expect(restored.Locator(".mock-assess__result")).ToHaveTextAsync("Your grade: Partially");
        Assert.Equal(0, await restored.Locator(".self__grades").CountAsync());
        Assert.Equal(items - 1, await tab.Locator(".mock-assess__item:has(.self__grades)").CountAsync());
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Mock_SecondTab_IsReadOnly()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage first = await OpenAsync(context);
        await StartAsync(first);
        IPage second = await OpenAsync(context);
        await WaitForPhaseAsync(second, "answering");

        await AnswerCurrentAsync(first, AnonymousProgress);
        await second.Locator(".jump__btn").Nth(1).ClickAsync();

        await Assertions.Expect(second.Locator(".mock__notice")).ToHaveTextAsync(Resource("Mock.OtherTab"));
        await Assertions.Expect(second.Locator(".mock")).ToHaveAttributeAsync("data-mock-read-only", "true");
        await Assertions.Expect(second.Locator(".mock__finish")).ToBeDisabledAsync();
        long revision = await RevisionAsync(first, AnonymousProgress);
        await first.Locator(".mock__next").ClickAsync();
        await WaitForRevisionAsync(first, AnonymousProgress, revision);
        Assert.Equal(0, await first.Locator(".mock__notice").CountAsync());
        host.AssertNoCspViolations(first);
        host.AssertNoCspViolations(second);
    }

    [Fact]
    public async Task Mock_SignedIn_SummaryStored()
    {
        const long telegramId = 990_400_001L;
        await using IBrowserContext context = await NewContextAsync();
        await host.SignInAsync(context, new FakeTelegramIdentity(telegramId, "Mock Online", "mock_online"));
        IPage tab = await OpenAsync(context);
        string progress = $"dit.mock.v1.{telegramId}.active";
        await StartAsync(tab);
        await AnswerCurrentAsync(tab, progress);
        await FinishAsync(tab);
        await AssessAllAsync(tab, "didnt-know");

        await WaitForServerCountAsync("mock_sessions", telegramId, 1, TimeSpan.FromSeconds(30));
        int graded = await tab.Locator(".mock-summary__list .practice-summary__mark:not(.practice-summary__mark--none)").CountAsync();
        await WaitForServerCountAsync("answers", telegramId, graded, TimeSpan.FromSeconds(30));
        Assert.Null(await StoredAsync(tab, progress));
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Mock_OfflineFinish_SummaryStoredAfterReopen()
    {
        const long telegramId = 990_400_002L;
        await using IBrowserContext context = await NewContextAsync();
        await host.SignInAsync(context, new FakeTelegramIdentity(telegramId, "Mock Offline", "mock_offline"));
        IPage tab = await OpenAsync(context);
        string progress = $"dit.mock.v1.{telegramId}.active";
        await StartAsync(tab);

        await context.SetOfflineAsync(true);
        await AnswerCurrentAsync(tab, progress);
        await FinishAsync(tab);
        await AssessAllAsync(tab, "knew");
        await Assertions.Expect(tab.Locator(".mock-summary")).ToBeVisibleAsync();
        JsonElement pending = (await StoredAsync(tab, $"dit.mock.v1.pending.{telegramId}"))!.Value;
        Assert.Equal(1, pending.GetProperty("items").GetArrayLength());
        Assert.True(await StoredCountAsync(tab, $"dit.outbox.v1.u.{telegramId}") >= 1);
        Assert.Equal(0, await ServerCountAsync("mock_sessions", telegramId));
        host.AssertNoCspViolations(tab);
        await tab.CloseAsync();

        await context.SetOfflineAsync(false);
        IPage reopened = await OpenAsync(context);

        await WaitForServerCountAsync("mock_sessions", telegramId, 1, TimeSpan.FromSeconds(30));
        await WaitForPhaseAsync(reopened, "setup");
        host.AssertNoCspViolations(reopened);
    }

    // Wraps window.ditStore as outbox.js defines it: outbox appends never complete, like a lock that is never granted.
    private const string StuckAppends =
        """
        (() => {
          let store;
          Object.defineProperty(window, 'ditStore', {
            configurable: true,
            get() { return store; },
            set(value) {
              const mutate = value.mutate;
              value.mutate = (op) => JSON.parse(op).op === 'append' ? new Promise(() => { }) : mutate(op);
              store = value;
            }
          });
        })();
        """;

    [Fact]
    public async Task Mock_OutboxWriteNeverCompletes_SummaryStillShows()
    {
        await using IBrowserContext context = await NewContextAsync();
        await context.AddInitScriptAsync(StuckAppends);
        IPage tab = await OpenAsync(context);
        await StartAsync(tab);
        await AnswerCurrentAsync(tab, AnonymousProgress);
        await FinishAsync(tab);
        DateTime finished = DateTime.UtcNow;

        // Grades rather than skips: at least one answer must reach the stuck append.
        await AssessAllAsync(tab, "knew");

        await Assertions.Expect(tab.Locator(".mock-summary")).ToBeVisibleAsync();
        await Assertions.Expect(tab.Locator(".mock__notice--slow")).ToHaveTextAsync(Resource("Mock.Slow"));
        Assert.True(DateTime.UtcNow - finished < TimeSpan.FromSeconds(30));
        Assert.Equal(0, await StoredCountAsync(tab, AnonymousOutbox));
        // The hand-over is not complete, so the mock stays resumable.
        string sessionId = (await StoredAsync(tab, AnonymousProgress))!.Value.GetProperty("sessionId").GetString()!;

        // A new interview may not replace results that are not durable: the summary stays and says why.
        await tab.Locator(".mock-summary__new").ClickAsync();

        await Assertions.Expect(tab.Locator(".mock-summary__pending")).ToHaveTextAsync(
            Resource("Mock.HandoffPending"), new LocatorAssertionsToHaveTextOptions { Timeout = Wait });
        await WaitForPhaseAsync(tab, "summary");
        Assert.Equal(0, await tab.Locator(".mock-setup").CountAsync());
        Assert.Equal(sessionId, (await StoredAsync(tab, AnonymousProgress))!.Value.GetProperty("sessionId").GetString());
        await Assertions.Expect(tab.Locator("#mock-summary-heading")).ToBeFocusedAsync();
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Mock_CompletedAnonymousMockLeftAtSignIn_IsOfferedOnTheProfile()
    {
        const long telegramId = 990_400_011L;
        await using IBrowserContext context = await NewContextAsync();
        IPage anonymous = await host.NewPageAsync(context);
        // This tab's outbox appends never finish, so the finished mock stays in its progress document, not handed over.
        await anonymous.AddInitScriptAsync(StuckAppends);
        await anonymous.GotoAsync(MockPath);
        await anonymous.Locator(ReadyIsland).WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        await StartAsync(anonymous);
        await AnswerCurrentAsync(anonymous, AnonymousProgress);
        await FinishAsync(anonymous);
        await AssessAllAsync(anonymous, "knew");
        await Assertions.Expect(anonymous.Locator(".mock-summary")).ToBeVisibleAsync();
        JsonElement stranded = (await StoredAsync(anonymous, AnonymousProgress))!.Value;
        Assert.False(stranded.GetProperty("completion").GetProperty("answersQueued").GetBoolean());
        int answers = stranded.GetProperty("completion").GetProperty("answers").GetArrayLength();
        Assert.True(answers > 0);
        await anonymous.CloseAsync();

        await host.SignInAsync(context, new FakeTelegramIdentity(telegramId, "Mock Stranded", "mock_stranded"));
        IPage tab = await host.NewPageAsync(context);
        // The profile, not Mock: any signed-in island start hands a finished anonymous mock over to the import.
        await tab.GotoAsync("/en/dotnet/profile");
        await tab.Locator(".profile").WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });

        await Assertions.Expect(tab.Locator(".import-panel")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = Wait });
        Assert.Null(await StoredAsync(tab, AnonymousProgress));
        Assert.Equal(answers, await StoredCountAsync(tab, AnonymousOutbox));
        host.AssertNoCspViolations(tab);
    }

    private static Task<IJSHandle> WaitForFocusAsync(IPage tab, string selector) =>
        tab.WaitForFunctionAsync(
            "s => document.activeElement !== document.body && document.activeElement.matches(s)",
            selector,
            new PageWaitForFunctionOptions { Timeout = Wait });

    [Fact]
    public async Task Mock_Focus_FollowsStartAndAssessment()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await OpenAsync(context, "?topic=async");
        await ChooseAsync(tab);

        await tab.Locator(".mock-setup__start").FocusAsync();
        await tab.Keyboard.PressAsync("Enter");
        await WaitForPhaseAsync(tab, "prepared");
        await WaitForFocusAsync(tab, ".mock-warn");

        await tab.Locator(".mock-warn__anyway").FocusAsync();
        await tab.Keyboard.PressAsync("Enter");
        await WaitForPhaseAsync(tab, "answering");
        await WaitForFocusAsync(tab, ".qcard__title");

        await tab.Locator(".mock__finish").FocusAsync();
        await tab.Keyboard.PressAsync("Enter");
        await WaitForPhaseAsync(tab, "assessment");
        await WaitForFocusAsync(tab, "#mock-assess-heading");
        int items = await tab.Locator(".mock-assess__item").CountAsync();
        Assert.True(items >= 3, $"Expected at least 3 open questions, found {items}.");

        // A grade by pointer: focus goes to the next item's title, never to the page body.
        ILocator first = tab.Locator(".mock-assess__item").Nth(0);
        ILocator second = tab.Locator(".mock-assess__item").Nth(1);
        await first.Locator(".self__grade--knew").ClickAsync();
        await WaitForFocusAsync(tab, $".mock-assess__item[data-assess-index='{await second.GetAttributeAsync("data-assess-index")}'] .qcard__title");
        await Assertions.Expect(first.Locator(".mock-assess__result")).ToHaveTextAsync("Your grade: " + Resource("Self.Grade.Knew"));

        // A shortcut grades the item that holds focus, and focus moves on again.
        await tab.Keyboard.PressAsync("2");
        await Assertions.Expect(second.Locator(".mock-assess__result")).ToHaveTextAsync("Your grade: Partially");
        ILocator third = tab.Locator(".mock-assess__item").Nth(2);
        await WaitForFocusAsync(tab, $".mock-assess__item[data-assess-index='{await third.GetAttributeAsync("data-assess-index")}'] .qcard__title");

        // Skip by keyboard on the rest; the last one moves focus to the summary heading.
        while (await PhaseAsync(tab) == "assessment")
        {
            ILocator open = tab.Locator(".mock-assess__item:has(.self__grades)").First;
            await open.Locator(".self__grades .btn--quiet").FocusAsync();
            await tab.Keyboard.PressAsync("Enter");
            await tab.WaitForFunctionAsync(
                "() => document.activeElement !== document.body",
                null,
                new PageWaitForFunctionOptions { Timeout = Wait });
            await WaitForPhaseAsync(tab, "assessment", "summary");
            await tab.WaitForFunctionAsync(
                "() => !document.querySelector('.mock-assess .self__saving')",
                null,
                new PageWaitForFunctionOptions { Timeout = Wait });
        }

        await WaitForFocusAsync(tab, "#mock-summary-heading");
        Assert.False(await tab.EvaluateAsync<bool>("() => document.activeElement === document.body"));
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Mock_OtherCultureMock_CanBeEndedHere()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await host.NewPageAsync(context);
        await tab.GotoAsync("/ru/dotnet/mock");
        await tab.Locator(ReadyIsland).WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        await StartAsync(tab);
        await AnswerCurrentAsync(tab, AnonymousProgress);
        string sessionId = (await StoredAsync(tab, AnonymousProgress))!.Value.GetProperty("sessionId").GetString()!;

        await tab.GotoAsync(MockPath);
        await tab.Locator(ReadyIsland).WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        await WaitForPhaseAsync(tab, "elsewhere");
        Assert.Equal("/ru/dotnet/mock", await tab.Locator(".mock .island-state a.btn").GetAttributeAsync("href"));
        Assert.Equal(0, await tab.Locator(".qcard").CountAsync());

        await tab.Locator(".mock__end-elsewhere").ClickAsync();

        await WaitForPhaseAsync(tab, "setup");
        await WaitForFocusAsync(tab, "#mock-setup-heading");
        Assert.Null(await StoredAsync(tab, AnonymousProgress));
        Assert.Null(await StoredAsync(tab, $"dit.mock.v1.anon.{sessionId}.questions"));
        await StartAsync(tab);
        Assert.NotEqual(sessionId, (await StoredAsync(tab, AnonymousProgress))!.Value.GetProperty("sessionId").GetString());
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Mock_AnonymousMockLeftAtSignIn_CanBeEndedForImport()
    {
        const long telegramId = 990_400_010L;
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await OpenAsync(context);
        await StartAsync(tab);
        await tab.Locator(".qcard").WaitForAsync();
        // An automatically graded response becomes an answer; notes on an open question are skipped when it is ended.
        int expected = await tab.Locator(".opt--pick, .code-line, .typed__input").CountAsync() > 0 ? 1 : 0;
        await AnswerCurrentAsync(tab, AnonymousProgress);

        await host.SignInAsync(context, new FakeTelegramIdentity(telegramId, "Mock Carried", "mock_carried"));
        await tab.GotoAsync(MockPath);
        await tab.Locator(ReadyIsland).WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        await WaitForPhaseAsync(tab, "setup");
        await Assertions.Expect(tab.Locator(".mock__anonymous")).ToContainTextAsync(Resource("Mock.Anonymous.Unfinished"));

        await tab.Locator(".mock__anonymous-finish").ClickAsync();

        ILocator import = tab.Locator(".mock__anonymous a");
        await Assertions.Expect(import).ToHaveAttributeAsync("href", "/en/dotnet/profile#import");
        Assert.Null(await StoredAsync(tab, AnonymousProgress));
        Assert.Equal(expected, await StoredCountAsync(tab, AnonymousOutbox));
        Assert.Null(await StoredAsync(tab, $"dit.mock.v1.{telegramId}.active"));
        host.AssertNoCspViolations(tab);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(1280)]
    public async Task Mock_OptionColumns_FollowTheLayout(int width)
    {
        await using IBrowserContext context = await NewContextAsync(width: width);
        IPage tab = await OpenAsync(context);
        await StartAsync(tab);
        int count = await tab.Locator(".jump__btn").CountAsync();
        int checkedGrids = 0;
        for (int i = 0; i < count; i++)
        {
            await tab.Locator(".jump__btn").Nth(i).ClickAsync();
            await Assertions.Expect(tab.Locator(".jump__btn").Nth(i)).ToHaveAttributeAsync("aria-current", "step");
            ILocator grid = tab.Locator(".opts--pick");
            if (await grid.CountAsync() == 0)
            {
                continue;
            }

            string classes = await grid.GetAttributeAsync("class") ?? "";
            int fit = int.Parse(classes.Split(' ').Single(c => c.StartsWith("opts--fit-", StringComparison.Ordinal))["opts--fit-".Length..], CultureInfo.InvariantCulture);
            int options = await grid.Locator(".opt--pick").CountAsync();
            int columns = await grid.EvaluateAsync<int>("g => getComputedStyle(g).gridTemplateColumns.split(' ').length");
            int rows = await grid.EvaluateAsync<int>("g => new Set([...g.children].map(c => Math.round(c.getBoundingClientRect().top))).size");

            Assert.Equal(fit, columns);
            Assert.Equal((options + columns - 1) / columns, rows);
            checkedGrids++;
        }

        Assert.True(checkedGrids > 0, "No question with options in the mock.");
        host.AssertNoCspViolations(tab);
    }

    public static TheoryData<int, string> Layouts() => new()
    {
        { 320, "dark" },
        { 375, "dark" },
        { 768, "dark" },
        { 1280, "dark" },
        { 1920, "dark" },
        { 375, "light" },
        { 1280, "light" },
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task Mock_NoHorizontalOverflow(int width, string theme)
    {
        await using IBrowserContext context = await NewContextAsync(width: width, theme: theme);
        IPage tab = await OpenAsync(context);
        await StartAsync(tab, count: 20, timed: true);
        await AnswerCurrentAsync(tab, AnonymousProgress);
        await tab.EvaluateAsync("document.fonts.ready");

        await AssertNoOverflowAsync(tab, width, theme, "answering");
        Directory.CreateDirectory(ScreenshotDirectory);
        await tab.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(ScreenshotDirectory, $"mock-{width}-{theme}.png"), FullPage = true });

        await FinishAsync(tab);
        await AssertNoOverflowAsync(tab, width, theme, "assessment");
        await AssessAllAsync(tab, "partial");
        await AssertNoOverflowAsync(tab, width, theme, "summary");
        host.AssertNoCspViolations(tab);
    }

    private static async Task AssertNoOverflowAsync(IPage tab, int width, string theme, string state)
    {
        int scrollWidth = await tab.EvaluateAsync<int>("document.documentElement.scrollWidth");
        int innerWidth = await tab.EvaluateAsync<int>("window.innerWidth");
        Assert.True(scrollWidth <= innerWidth, $"mock {state} at {width} px ({theme}): scrollWidth {scrollWidth} > innerWidth {innerWidth}.");
    }

    [Theory]
    [InlineData(375, true, true)]
    [InlineData(1280, false, false)]
    [InlineData(1280, true, false)]
    public async Task Mock_TouchTargetsAtLeast44px(int width, bool touch, bool mobile)
    {
        await using IBrowserContext context = await NewContextAsync(width: width, touch: touch, mobile: mobile);
        IPage tab = await OpenAsync(context, "?topic=async");
        await ChooseAsync(tab, timed: true);
        await AssertTargetsAsync(tab, "setup");
        await tab.Locator(".mock-setup__start").ClickAsync();
        await WaitForPhaseAsync(tab, "prepared");
        await AssertTargetsAsync(tab, "prepared");
        await tab.Locator(".mock-warn__anyway").ClickAsync();
        await WaitForPhaseAsync(tab, "answering");
        await AssertTargetsAsync(tab, "answering");

        await FinishAsync(tab);
        await AssertTargetsAsync(tab, "assessment");
        await AssessAllAsync(tab, "knew");
        await AssertTargetsAsync(tab, "summary");
        host.AssertNoCspViolations(tab);
    }

    private static async Task AssertTargetsAsync(IPage tab, string state)
    {
        string[] undersized = await tab.EvaluateAsync<string[]>(
            """
            (min) => [...document.querySelectorAll('.island-root a[href], .island-root button, .island-root summary, .island-root textarea, .island-root select, .island-root input:not([type=radio]):not([type=checkbox]), .island-root label:has(> input)')]
              .filter(e => e.getClientRects().length > 0)
              .map(e => ({ e, r: e.getBoundingClientRect() }))
              .filter(x => x.r.width < min || x.r.height < min)
              .map(x => `${x.e.tagName.toLowerCase()}.${x.e.className} "${(x.e.textContent || '').trim().slice(0, 30)}" ${Math.round(x.r.width)}x${Math.round(x.r.height)}`)
            """,
            MinimumTarget);
        Assert.True(undersized.Length == 0, $"{state}: targets below {MinimumTarget} px: {string.Join("; ", undersized)}.");
    }
}
