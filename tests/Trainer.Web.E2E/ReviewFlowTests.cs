using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Playwright;
using Trainer.Domain.Grading;
using Trainer.TestSupport;

namespace Trainer.Web.E2E;

/// <summary>
/// Review in a real browser (spec 5.3): due questions only, the recall step before the options of a choice question,
/// flush-before-fetch so a just-answered question is not served again, the empty queue and the layout. Due states are
/// seeded through the app's own answer recorder.
/// </summary>
[Trait("Category", "E2E")]
public sealed class ReviewFlowTests(AppHostFixture host) : IClassFixture<AppHostFixture>
{
    private const string ReviewPath = "/en/dotnet/review";
    private const string ClosureId = "01KBZ3Q8N9V2K3M4P5R6S7T8WA";
    private const string SingleId = "01KBZ3Q8N9V2K3M4P5R6S7T8WC";
    private const int MinimumTarget = 44;

    private static readonly TimeSpan IslandTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Overdue = TimeSpan.FromDays(2);
    private static readonly string ScreenshotDirectory = Path.Combine(ContentWorkspace.RepositoryRoot(), "TestResults", "screenshots");

    private static long _nextTelegramId = 990_500_000L;

    private static float Wait => (float)IslandTimeout.TotalMilliseconds;

    private async Task<(IBrowserContext Context, long TelegramId)> SignedInContextAsync(
        int width = 1280, string theme = "dark", bool touch = false, bool mobile = false)
    {
        long telegramId = Interlocked.Increment(ref _nextTelegramId);
        IBrowserContext context = await host.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString(),
            Locale = "en-US",
            ViewportSize = new ViewportSize { Width = width, Height = 900 },
            HasTouch = touch,
            IsMobile = mobile,
        });
        await context.AddInitScriptAsync($"try {{ localStorage.setItem('dit.theme', '{theme}'); }} catch (e) {{ }}");
        await host.SignInAsync(context, new FakeTelegramIdentity(telegramId, "Review Tester", "review_tester"));
        return (context, telegramId);
    }

    private static readonly ConditionalWeakTable<IPage, ResponseTap> Taps = [];

    /// <summary>Opens Review and returns the body of the session response it fetched.</summary>
    private static async Task<JsonElement> OpenReviewAsync(IPage tab)
    {
        if (!Taps.TryGetValue(tab, out ResponseTap? tap))
        {
            tap = await ResponseTap.AttachAsync(tab, "**/api/sessions/review");
            Taps.Add(tab, tap);
        }

        int before = tap.Count;
        await tab.GotoAsync(ReviewPath);
        JsonElement session = await tap.WaitForAsync(before + 1);
        await tab.Locator(".qcard, .review-empty").First.WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        return session;
    }

    private static string[] QuestionIds(JsonElement session) =>
        [.. session.GetProperty("questions").EnumerateArray().Select(q => q.GetProperty("id").GetString()!)];

    private static string CorrectOption(JsonElement session, string questionId) =>
        session.GetProperty("questions").EnumerateArray()
            .First(q => q.GetProperty("id").GetString() == questionId)
            .GetProperty("options").EnumerateArray()
            .First(o => o.GetProperty("correct").GetBoolean())
            .GetProperty("id").GetString()!;

    private static int OptionCount(JsonElement session, string questionId) =>
        session.GetProperty("questions").EnumerateArray()
            .First(q => q.GetProperty("id").GetString() == questionId)
            .GetProperty("options").GetArrayLength();

    private static async Task<int> SavedAsync(IPage tab) =>
        int.Parse(await tab.Locator(".qs").GetAttributeAsync("data-answers-saved") ?? "0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Reveals the options, picks the correct one and answers; waits for the outbox write and the feedback.</summary>
    private static async Task AnswerCorrectlyAsync(IPage tab, JsonElement session, string questionId)
    {
        await tab.Locator(".recall__show").ClickAsync();
        int saved = await SavedAsync(tab);
        await tab.Locator($".opt--pick:has(input[value='{CorrectOption(session, questionId)}'])").ClickAsync();
        await tab.Locator(".review__answer").ClickAsync();
        await tab.Locator($".qs[data-answers-saved='{saved + 1}']").WaitForAsync();
        await tab.Locator(".feedback").WaitForAsync();
    }

    private static async Task<string> CurrentQuestionIdAsync(IPage tab, JsonElement session)
    {
        string title = (await tab.Locator(".qcard__title").InnerTextAsync()).Trim();
        return session.GetProperty("questions").EnumerateArray()
            .First(q => q.GetProperty("title").GetString() == title)
            .GetProperty("id").GetString()!;
    }

    private static string Resource(string key)
    {
        string path = Path.Combine(ContentWorkspace.RepositoryRoot(), "src", "Trainer.Web.Client", "Localization", "ClientResource.en.resx");
        return XDocument.Load(path).Descendants("data").Single(d => (string?)d.Attribute("name") == key).Element("value")!.Value;
    }

    private static async Task AssertFocusNotOnBodyAsync(IPage tab, string after) =>
        Assert.False(
            await tab.EvaluateAsync<bool>("() => document.activeElement === null || document.activeElement === document.body"),
            $"Focus fell to the body after {after}.");

    [Fact]
    public async Task Review_DueQuestion_RecallThenOptions()
    {
        (IBrowserContext context, long telegramId) = await SignedInContextAsync();
        await using IBrowserContext owned = context;
        await host.RecordAnswersAsync(telegramId, (SingleId, Grade.DidntKnow, Overdue));
        IPage tab = await host.NewPageAsync(context);
        List<string> batches = [];
        tab.Request += (_, request) =>
        {
            if (new Uri(request.Url).AbsolutePath == "/api/answers/batch")
            {
                lock (batches)
                {
                    batches.Add(request.PostData ?? "");
                }
            }
        };

        JsonElement session = await OpenReviewAsync(tab);

        Assert.Equal([SingleId], QuestionIds(session));
        await Assertions.Expect(tab.Locator(".recall__ask")).ToHaveTextAsync(Resource("Review.Recall.Ask"));
        Assert.Equal(0, await tab.Locator(".opt--pick").CountAsync());
        Assert.Equal(0, await tab.Locator(".review__answer").CountAsync());

        // Pressing 1 while the options are hidden picks nothing; Enter reveals them.
        await tab.Locator(".qcard__title").FocusAsync();
        await tab.Keyboard.PressAsync("1");
        await tab.Keyboard.PressAsync("Enter");
        await Assertions.Expect(tab.Locator(".opt--pick")).ToHaveCountAsync(OptionCount(session, SingleId));
        Assert.Equal(0, await tab.Locator(".opt--pick input:checked").CountAsync());
        await AssertFocusNotOnBodyAsync(tab, "showing the options");

        int saved = await SavedAsync(tab);
        await tab.Locator($".opt--pick:has(input[value='{CorrectOption(session, SingleId)}'])").ClickAsync();
        await tab.Locator(".review__answer").ClickAsync();
        await tab.Locator($".qs[data-answers-saved='{saved + 1}']").WaitForAsync();
        await Assertions.Expect(tab.Locator(".feedback__verdict--knew")).ToBeVisibleAsync();
        await AssertFocusNotOnBodyAsync(tab, "answering");

        await host.WaitForServerAnswersAsync(telegramId, 2);
        lock (batches)
        {
            Assert.Contains(batches, b => b.Contains("\"mode\":\"review\"", StringComparison.Ordinal));
        }

        await tab.Locator(".review__next").ClickAsync();
        await Assertions.Expect(tab.Locator(".review-summary")).ToBeVisibleAsync();
        await AssertFocusNotOnBodyAsync(tab, "finishing");
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Review_EmptyQueue_ShowsNothingDue()
    {
        (IBrowserContext context, _) = await SignedInContextAsync();
        await using IBrowserContext owned = context;
        IPage tab = await host.NewPageAsync(context);

        JsonElement session = await OpenReviewAsync(tab);

        Assert.Empty(QuestionIds(session));
        ILocator empty = tab.Locator(".review-empty");
        await Assertions.Expect(empty).ToContainTextAsync(Resource("Review.Empty"));
        Assert.Equal("en/dotnet/practice", await empty.Locator(".review-empty__practice").GetAttributeAsync("href"));
        Assert.Equal("en/dotnet/profile", await empty.Locator(".review-empty__profile").GetAttributeAsync("href"));
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Review_SecondSession_DoesNotRepeatJustAnswered()
    {
        (IBrowserContext context, long telegramId) = await SignedInContextAsync();
        await using IBrowserContext owned = context;
        await host.RecordAnswersAsync(telegramId, (SingleId, Grade.DidntKnow, Overdue), (ClosureId, Grade.DidntKnow, Overdue));
        IPage tab = await host.NewPageAsync(context);

        // Only the next page's flush-before-fetch can deliver the answer: the background sync of this one never reaches the server.
        await tab.RouteAsync("**/api/answers/batch", route => route.AbortAsync());
        JsonElement first = await OpenReviewAsync(tab);
        Assert.Equal(2, QuestionIds(first).Length);
        string answered = await CurrentQuestionIdAsync(tab, first);
        await AnswerCorrectlyAsync(tab, first, answered);
        Assert.Equal(2, await host.ServerAnswerCountAsync(telegramId));

        await tab.UnrouteAsync("**/api/answers/batch");
        JsonElement second = await OpenReviewAsync(tab);

        Assert.Equal(3, await host.ServerAnswerCountAsync(telegramId));
        string remaining = Assert.Single(QuestionIds(second));
        Assert.NotEqual(answered, remaining);
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Review_CheckForMore_DoesNotServeAnAnswerStillPending()
    {
        (IBrowserContext context, long telegramId) = await SignedInContextAsync();
        await using IBrowserContext owned = context;
        await host.RecordAnswersAsync(telegramId, (SingleId, Grade.DidntKnow, Overdue));
        IPage tab = await host.NewPageAsync(context);
        JsonElement session = await OpenReviewAsync(tab);

        // The answer cannot reach the server, which therefore still counts the question as due.
        await tab.RouteAsync("**/api/answers/batch", route => route.AbortAsync());
        await AnswerCorrectlyAsync(tab, session, SingleId);
        await tab.Locator(".review__next").ClickAsync();
        await tab.Locator(".review-summary__again").ClickAsync();

        await Assertions.Expect(tab.Locator(".review-empty")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = Wait });
        Assert.Equal(1, await host.ServerAnswerCountAsync(telegramId));
        await AssertFocusNotOnBodyAsync(tab, "checking for more");
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
    public async Task Review_NoHorizontalOverflow(int width, string theme)
    {
        (IBrowserContext context, long telegramId) = await SignedInContextAsync(width, theme);
        await using IBrowserContext owned = context;
        await host.RecordAnswersAsync(telegramId, (SingleId, Grade.DidntKnow, Overdue));
        IPage tab = await host.NewPageAsync(context);
        JsonElement session = await OpenReviewAsync(tab);
        await AssertNoOverflowAsync(tab, $"review-recall-{width}-{theme}");

        await AnswerCorrectlyAsync(tab, session, SingleId);
        await AssertNoOverflowAsync(tab, $"review-{width}-{theme}");
        Assert.True(await tab.Locator(".review__next").IsVisibleAsync());
        host.AssertNoCspViolations(tab);
    }

    private static async Task AssertNoOverflowAsync(IPage tab, string name)
    {
        await tab.EvaluateAsync("document.fonts.ready");
        int scrollWidth = await tab.EvaluateAsync<int>("document.documentElement.scrollWidth");
        int innerWidth = await tab.EvaluateAsync<int>("window.innerWidth");
        Directory.CreateDirectory(ScreenshotDirectory);
        await tab.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(ScreenshotDirectory, name + ".png"), FullPage = true });
        Assert.True(scrollWidth <= innerWidth, $"{name}: scrollWidth {scrollWidth} > innerWidth {innerWidth}.");
    }

    [Fact]
    public async Task Review_TouchTargetsAtLeast44px()
    {
        (IBrowserContext context, long telegramId) = await SignedInContextAsync(375, touch: true, mobile: true);
        await using IBrowserContext owned = context;
        await host.RecordAnswersAsync(telegramId, (SingleId, Grade.DidntKnow, Overdue));
        IPage tab = await host.NewPageAsync(context);
        JsonElement session = await OpenReviewAsync(tab);
        await tab.Locator("details.side summary").ClickAsync();
        await AssertTargetsAsync(tab, "recall");

        await tab.Locator(".recall__show").ClickAsync();
        await AssertTargetsAsync(tab, "options");
        await tab.Locator($".opt--pick:has(input[value='{CorrectOption(session, SingleId)}'])").ClickAsync();
        await tab.Locator(".review__answer").ClickAsync();
        await tab.Locator(".feedback").WaitForAsync();
        await AssertTargetsAsync(tab, "feedback");
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
