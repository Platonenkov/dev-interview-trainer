using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Playwright;
using Trainer.Domain.Grading;
using Trainer.TestSupport;

namespace Trainer.Web.E2E;

/// <summary>
/// The profile in a real browser (ADR 0010): statistics after flush-before-fetch, refused mock summaries, and the
/// anonymous import (spec 4, 11.4): answers given before sign-in stay in the browser until the user chooses Import or
/// Discard on the profile, and signed-in public pages point there.
/// </summary>
[Trait("Category", "E2E")]
public sealed class ProfileAndImportTests(AppHostFixture host) : IClassFixture<AppHostFixture>
{
    private const string ProfilePath = "/en/dotnet/profile";
    private const string AnonymousOutbox = "dit.outbox.v1.anon";
    private const string ClosureId = "01KBZ3Q8N9V2K3M4P5R6S7T8WA";
    private const string SingleId = "01KBZ3Q8N9V2K3M4P5R6S7T8WC";
    private const int MinimumTarget = 44;

    private static readonly TimeSpan IslandTimeout = TimeSpan.FromSeconds(60);
    private static readonly string ScreenshotDirectory = Path.Combine(ContentWorkspace.RepositoryRoot(), "TestResults", "screenshots");

    private static long _nextTelegramId = 990_600_000L;

    private static float Wait => (float)IslandTimeout.TotalMilliseconds;

    private async Task<IBrowserContext> NewContextAsync(int width = 1280, string theme = "dark", bool touch = false, bool mobile = false)
    {
        IBrowserContext context = await host.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString(),
            Locale = "en-US",
            ViewportSize = new ViewportSize { Width = width, Height = 900 },
            HasTouch = touch,
            IsMobile = mobile,
        });
        await context.AddInitScriptAsync($"try {{ localStorage.setItem('theme', '{theme}'); }} catch (e) {{ }}");
        return context;
    }

    private async Task<long> SignInAsync(IBrowserContext context)
    {
        long telegramId = Interlocked.Increment(ref _nextTelegramId);
        await host.SignInAsync(context, new FakeTelegramIdentity(telegramId, "Profile Tester", "profile_tester"));
        return telegramId;
    }

    private static async Task OpenProfileAsync(IPage tab)
    {
        await tab.GotoAsync(ProfilePath);
        await tab.Locator(".profile").WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
    }

    private static async Task<int> StoredCountAsync(IPage tab, string key) =>
        await tab.EvaluateAsync<int>("k => { const v = localStorage.getItem(k); return v === null ? 0 : JSON.parse(v).answers.length; }", key);

    private static string Resource(string key)
    {
        string path = Path.Combine(ContentWorkspace.RepositoryRoot(), "src", "Trainer.Web.Client", "Localization", "ClientResource.en.resx");
        return XDocument.Load(path).Descendants("data").Single(d => (string?)d.Attribute("name") == key).Element("value")!.Value;
    }

    private static async Task AssertFocusNotOnBodyAsync(IPage tab, string after) =>
        Assert.False(
            await tab.EvaluateAsync<bool>("() => document.activeElement === null || document.activeElement === document.body"),
            $"Focus fell to the body after {after}.");

    /// <summary>Answers the Practice question on screen anonymously (the first option) and waits for its outbox write.</summary>
    private static async Task AnswerAnonymouslyAsync(IPage tab, string questionId)
    {
        await tab.GotoAsync($"/en/dotnet/practice?first={questionId}");
        await tab.Locator(".opt--pick").First.ClickAsync(new LocatorClickOptions { Timeout = Wait });
        await tab.Locator(".practice__answer").ClickAsync();
        await tab.Locator(".qs[data-answers-saved='1']").WaitForAsync();
    }

    /// <summary>Two valid anonymous outbox entries, written before the app's scripts run.</summary>
    private static string AnonymousEntries(DateTimeOffset at) =>
        "{\"version\":1,\"answers\":["
        + $"{{\"clientAnswerId\":\"01KC0000000000000000000001\",\"questionId\":\"{SingleId}\",\"grade\":\"knew\",\"usedHint\":false,\"mode\":\"practice\",\"answeredAt\":\"{at.ToString("O", CultureInfo.InvariantCulture)}\"}},"
        + $"{{\"clientAnswerId\":\"01KC0000000000000000000002\",\"questionId\":\"{ClosureId}\",\"grade\":\"didnt-know\",\"usedHint\":false,\"mode\":\"practice\",\"answeredAt\":\"{at.ToString("O", CultureInfo.InvariantCulture)}\"}}"
        + "]}";

    [Fact]
    public async Task Profile_ShowsStreakNextDueAndTopicProgress()
    {
        await using IBrowserContext context = await NewContextAsync();
        long telegramId = await SignInAsync(context);
        await host.RecordAnswersAsync(
            telegramId,
            (SingleId, Grade.Knew, TimeSpan.FromHours(1)),
            (ClosureId, Grade.DidntKnow, TimeSpan.FromDays(2)));
        IPage tab = await host.NewPageAsync(context);

        ResponseTap profileResponses = await ResponseTap.AttachAsync(tab, "**/api/profile*");

        await OpenProfileAsync(tab);

        // Answer days are server days in the user's offset, so the streak (1 or 2 near midnight) is read from the response.
        JsonElement profile = await profileResponses.WaitForAsync(1);
        int streak = profile.GetProperty("streakDays").GetInt32();
        Assert.InRange(streak, 1, 2);
        await Assertions.Expect(tab.Locator(".profile__streak")).ToHaveTextAsync(
            Resource(streak == 1 ? "Profile.Streak.One" : "Profile.Streak.Many").Replace("{0}", streak.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
        await Assertions.Expect(tab.Locator(".profile__total")).ToHaveTextAsync("2");
        ILocator due = tab.Locator(".profile__due a");
        Assert.Equal("/en/dotnet/review", await due.GetAttributeAsync("href"));
        await Assertions.Expect(due).ToContainTextAsync("1");
        Assert.NotNull(await tab.Locator(".profile__next time").GetAttributeAsync("datetime"));

        ILocator async = tab.Locator(".progress-bars--topics .progress-bars__item[data-key='async']");
        Assert.Equal("1", await async.Locator("meter").GetAttributeAsync("value"));
        await Assertions.Expect(async).ToContainTextAsync("1 / ");
        ILocator middle = tab.Locator(".progress-bars--levels .progress-bars__item[data-key='middle'] meter");
        Assert.Equal("1", await middle.GetAttributeAsync("value"));
        Assert.Equal(5, await tab.Locator(".progress-bars--levels .progress-bars__item").CountAsync());
        Assert.Equal(0, await tab.Locator("#import").CountAsync());
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Profile_ShowsFailedSummaryAndDiscards()
    {
        await using IBrowserContext context = await NewContextAsync();
        long telegramId = await SignInAsync(context);
        string key = $"dit.mock.v1.failed.{telegramId}";
        string item = "{\"version\":1,\"items\":[{\"mockSessionId\":\"01KC0000000000000000000009\",\"reason\":\"validation\",\"summary\":"
            + "{\"stack\":\"dotnet\",\"level\":\"middle\",\"questionCount\":1,\"timed\":false,\"startedAt\":\"2026-11-10T10:00:00+00:00\","
            + "\"finishedAt\":\"2026-11-10T10:05:00+00:00\",\"topics\":[{\"topic\":\"async\",\"knew\":1,\"partial\":0,\"didntKnow\":0,\"unanswered\":0}]}}]}";
        await context.AddInitScriptAsync($"if (!sessionStorage.getItem('seeded')) {{ localStorage.setItem('{key}', '{item}'); sessionStorage.setItem('seeded', '1'); }}");
        IPage tab = await host.NewPageAsync(context);

        await OpenProfileAsync(tab);

        ILocator failed = tab.Locator(".profile__failed li");
        await Assertions.Expect(failed).ToHaveCountAsync(1);
        await Assertions.Expect(failed).ToContainTextAsync(Resource("Profile.Failed.Reason.validation"));
        await failed.Locator(".profile__discard").ClickAsync();

        await Assertions.Expect(tab.Locator(".profile__failed li")).ToHaveCountAsync(0);
        await Assertions.Expect(tab.Locator(".profile__notice")).ToHaveTextAsync(Resource("Profile.Failed.Discarded"));
        await Assertions.Expect(tab.Locator(".profile__notice")).ToBeFocusedAsync();
        Assert.Null(await tab.EvaluateAsync<string?>("k => localStorage.getItem(k)", key));
        await AssertFocusNotOnBodyAsync(tab, "discarding a summary");
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task AnonymousAnswers_ImportedAfterSignIn_OnlyWhenChosen()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await host.NewPageAsync(context);
        await AnswerAnonymouslyAsync(tab, SingleId);
        await AnswerAnonymouslyAsync(tab, ClosureId);
        Assert.Equal(2, await StoredCountAsync(tab, AnonymousOutbox));

        long telegramId = await SignInAsync(context);

        // A signed-in public page reveals its server-rendered notice; nothing has been imported.
        await tab.GotoAsync("/en/about");
        ILocator notice = tab.Locator(".import-notice");
        await Assertions.Expect(notice).ToBeVisibleAsync();
        Assert.Equal("/en/dotnet/profile#import", await notice.Locator("a").GetAttributeAsync("href"));

        // An island page of the same account shows its own notice and still imports nothing.
        await tab.GotoAsync("/en/dotnet/practice");
        ILocator islandNotice = tab.Locator(".island-import-notice a");
        await Assertions.Expect(islandNotice).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = Wait });
        Assert.Equal("/en/dotnet/profile#import", await islandNotice.GetAttributeAsync("href"));

        await OpenProfileAsync(tab);
        ILocator panel = tab.Locator("#import");
        await Assertions.Expect(panel).ToContainTextAsync(Resource("Import.Found.Many").Replace("{0}", "2", StringComparison.Ordinal));
        Assert.Equal(0, await host.ServerAnswerCountAsync(telegramId));
        Assert.Equal(2, await StoredCountAsync(tab, AnonymousOutbox));

        await panel.Locator(".import-panel__import").ClickAsync();

        await Assertions.Expect(tab.Locator(".import-panel__result")).ToBeVisibleAsync();
        await host.WaitForServerAnswersAsync(telegramId, 2);
        Assert.Equal(0, await StoredCountAsync(tab, AnonymousOutbox));
        await AssertFocusNotOnBodyAsync(tab, "importing");
        await Assertions.Expect(tab.Locator(".profile__total")).ToHaveTextAsync("2");

        // Nothing is left to offer.
        await tab.GotoAsync("/en/about");
        await Assertions.Expect(tab.Locator(".import-notice")).ToBeHiddenAsync();
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task AnonymousAnswers_Discard_ClearsAfterConfirmation()
    {
        await using IBrowserContext context = await NewContextAsync();
        await context.AddInitScriptAsync(
            $"if (!sessionStorage.getItem('seeded')) {{ localStorage.setItem('{AnonymousOutbox}', '{AnonymousEntries(DateTimeOffset.UtcNow.AddMinutes(-5))}'); sessionStorage.setItem('seeded', '1'); }}");
        long telegramId = await SignInAsync(context);
        IPage tab = await host.NewPageAsync(context);
        await OpenProfileAsync(tab);
        ILocator panel = tab.Locator("#import");
        await Assertions.Expect(panel).ToContainTextAsync(Resource("Import.Found.Many").Replace("{0}", "2", StringComparison.Ordinal));

        await panel.Locator(".import-panel__discard").ClickAsync();
        ILocator confirm = panel.Locator(".import-panel__confirm");
        await Assertions.Expect(confirm).ToBeVisibleAsync();
        await AssertFocusNotOnBodyAsync(tab, "asking to confirm");
        Assert.Equal(2, await StoredCountAsync(tab, AnonymousOutbox));

        // Cancel keeps everything; confirming clears it.
        await panel.Locator(".import-panel__cancel").ClickAsync();
        await Assertions.Expect(confirm).ToHaveCountAsync(0);
        Assert.Equal(2, await StoredCountAsync(tab, AnonymousOutbox));
        await panel.Locator(".import-panel__discard").ClickAsync();
        await confirm.Locator(".import-panel__confirm-discard").ClickAsync();

        await Assertions.Expect(tab.Locator(".import-panel__result")).ToHaveTextAsync(Resource("Import.Discarded"));
        Assert.Equal(0, await StoredCountAsync(tab, AnonymousOutbox));
        await AssertFocusNotOnBodyAsync(tab, "discarding");
        await tab.WaitForTimeoutAsync(1000);
        Assert.Equal(0, await host.ServerAnswerCountAsync(telegramId));
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task AnonymousAnswers_SourceRemovalFails_ReportsThemStillHereThenCompletes()
    {
        await using IBrowserContext context = await NewContextAsync();
        await context.AddInitScriptAsync(
            $"if (!sessionStorage.getItem('seeded')) {{ localStorage.setItem('{AnonymousOutbox}', '{AnonymousEntries(DateTimeOffset.UtcNow.AddMinutes(-5))}'); sessionStorage.setItem('seeded', '1'); }}");
        long telegramId = await SignInAsync(context);
        IPage tab = await host.NewPageAsync(context);
        await OpenProfileAsync(tab);
        ILocator panel = tab.Locator("#import");
        await Assertions.Expect(panel).ToContainTextAsync(Resource("Import.Found.Many").Replace("{0}", "2", StringComparison.Ordinal));

        // Emptying the anonymous key fails twice: in the move, after the destination was written, and in the immediate
        // retry of its cleanup.
        await tab.EvaluateAsync(
            """
            (key) => {
              const removeItem = Storage.prototype.removeItem;
              let failures = 2;
              Storage.prototype.removeItem = function (k) {
                if (k === key && failures > 0) { failures--; throw new DOMException('injected', 'QuotaExceededError'); }
                return removeItem.call(this, k);
              };
            }
            """,
            AnonymousOutbox);
        await panel.Locator(".import-panel__import").ClickAsync();

        await Assertions.Expect(tab.Locator(".import-panel__result")).ToHaveTextAsync(
            Resource("Import.Incomplete.Many").Replace("{0}", "2", StringComparison.Ordinal));
        Assert.Equal(2, await StoredCountAsync(tab, AnonymousOutbox));
        await Assertions.Expect(panel.Locator(".import-panel__import")).ToBeEnabledAsync();

        await panel.Locator(".import-panel__import").ClickAsync();

        await Assertions.Expect(tab.Locator(".import-panel__result")).ToHaveTextAsync(
            Resource("Import.Imported.Many").Replace("{0}", "2", StringComparison.Ordinal));
        Assert.Equal(0, await StoredCountAsync(tab, AnonymousOutbox));
        await host.WaitForServerAnswersAsync(telegramId, 2);
        await AssertFocusNotOnBodyAsync(tab, "importing again");
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
    public async Task Profile_NoHorizontalOverflow(int width, string theme)
    {
        await using IBrowserContext context = await NewContextAsync(width, theme);
        await context.AddInitScriptAsync(
            $"if (!sessionStorage.getItem('seeded')) {{ localStorage.setItem('{AnonymousOutbox}', '{AnonymousEntries(DateTimeOffset.UtcNow.AddMinutes(-5))}'); sessionStorage.setItem('seeded', '1'); }}");
        long telegramId = await SignInAsync(context);
        await host.RecordAnswersAsync(telegramId, (SingleId, Grade.Knew, TimeSpan.FromHours(1)), (ClosureId, Grade.DidntKnow, TimeSpan.FromDays(2)));
        IPage tab = await host.NewPageAsync(context);
        await OpenProfileAsync(tab);
        await tab.Locator("#import").WaitForAsync();
        await tab.Locator(".profile__stats").WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        await tab.EvaluateAsync("document.fonts.ready");

        int scrollWidth = await tab.EvaluateAsync<int>("document.documentElement.scrollWidth");
        int innerWidth = await tab.EvaluateAsync<int>("window.innerWidth");
        Directory.CreateDirectory(ScreenshotDirectory);
        await tab.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(ScreenshotDirectory, $"profile-{width}-{theme}.png"), FullPage = true });

        Assert.True(scrollWidth <= innerWidth, $"profile at {width} px ({theme}): scrollWidth {scrollWidth} > innerWidth {innerWidth}.");

        // The public page with the revealed notice fits as well.
        await tab.GotoAsync("/en/dotnet");
        await Assertions.Expect(tab.Locator(".import-notice")).ToBeVisibleAsync();
        await tab.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(ScreenshotDirectory, $"import-notice-{width}-{theme}.png") });
        scrollWidth = await tab.EvaluateAsync<int>("document.documentElement.scrollWidth");
        Assert.True(scrollWidth <= innerWidth, $"stack home with the import notice at {width} px ({theme}): scrollWidth {scrollWidth} > innerWidth {innerWidth}.");
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Profile_TouchTargetsAtLeast44px()
    {
        await using IBrowserContext context = await NewContextAsync(375, touch: true, mobile: true);
        await context.AddInitScriptAsync(
            $"if (!sessionStorage.getItem('seeded')) {{ localStorage.setItem('{AnonymousOutbox}', '{AnonymousEntries(DateTimeOffset.UtcNow.AddMinutes(-5))}'); sessionStorage.setItem('seeded', '1'); }}");
        long telegramId = await SignInAsync(context);
        await host.RecordAnswersAsync(telegramId, (ClosureId, Grade.DidntKnow, TimeSpan.FromDays(2)));
        IPage tab = await host.NewPageAsync(context);
        await OpenProfileAsync(tab);
        await tab.Locator("#import").WaitForAsync();
        await tab.Locator(".profile__stats").WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        await AssertTargetsAsync(tab, "profile");

        await tab.Locator(".import-panel__discard").ClickAsync();
        await AssertTargetsAsync(tab, "confirm");

        await tab.GotoAsync("/en/dotnet");
        string[] undersized = await UndersizedAsync(tab, ".import-notice a");
        Assert.True(undersized.Length == 0, $"import notice: {string.Join("; ", undersized)}.");
        host.AssertNoCspViolations(tab);
    }

    private static async Task AssertTargetsAsync(IPage tab, string state)
    {
        string[] undersized = await UndersizedAsync(
            tab,
            ".island-root a[href], .island-root button, .island-root summary, .island-root textarea, .island-root select, .island-root input:not([type=radio]):not([type=checkbox]), .island-root label:has(> input)");
        Assert.True(undersized.Length == 0, $"{state}: targets below {MinimumTarget} px: {string.Join("; ", undersized)}.");
    }

    private static Task<string[]> UndersizedAsync(IPage tab, string selector) =>
        tab.EvaluateAsync<string[]>(
            """
            ([selector, min]) => [...document.querySelectorAll(selector)]
              .filter(e => e.getClientRects().length > 0)
              .map(e => ({ e, r: e.getBoundingClientRect() }))
              .filter(x => x.r.width < min || x.r.height < min)
              .map(x => `${x.e.tagName.toLowerCase()}.${x.e.className} "${(x.e.textContent || '').trim().slice(0, 30)}" ${Math.round(x.r.width)}x${Math.round(x.r.height)}`)
            """,
            new object[] { selector, MinimumTarget });
}
