using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Reflection;
using System.Resources;
using Microsoft.Playwright;
using Trainer.Domain.Feedback;
using Trainer.Domain.Grading;
using Trainer.TestSupport;
using Trainer.Web.Caching;

namespace Trainer.Web.E2E;

/// <summary>The layout and accessibility sweep over every island page, sign-in and admin share one host.</summary>
[CollectionDefinition(Name)]
public sealed class IslandSweepDefinition : ICollectionFixture<IslandSweepFixture>
{
    public const string Name = "Island sweep";
}

/// <summary>
/// The input devices of the browser: a touch phone or tablet (<c>pointer: coarse</c>), a mouse, or a laptop whose primary
/// pointer is a mouse and which also has a touch screen. Chromium cannot report <c>pointer: fine</c> together with
/// <c>any-pointer: coarse</c> (its touch emulation switches both to coarse, and <c>Emulation.setEmulatedMedia</c> ignores
/// pointer features), so the hybrid keeps the fine pointer and exposes touch support to scripts
/// (<c>navigator.maxTouchPoints</c>, <c>ontouchstart</c>).
/// </summary>
public enum PointerKind
{
    Coarse,
    Fine,
    FineWithTouch,
}

/// <summary>One rendering of a swept page: which page, how wide, which theme, which pointer and which culture.</summary>
public sealed record ProbeKey(string Page, int Width, string Theme, PointerKind Device, string Culture)
{
    /// <summary>The pointer of the devices that have this width: touch phones and tablets, mouse-driven desktops.</summary>
    public static PointerKind NaturalPointer(int width) => width < 1024 ? PointerKind.Coarse : PointerKind.Fine;

    public static ProbeKey Natural(string page, int width, string theme) => new(page, width, theme, NaturalPointer(width), "en");
}

/// <summary>A resource string a page must show in its own culture, located by a selector (and an attribute, or the text).</summary>
public sealed record LanguageCheck(string Selector, string? Attribute, bool ClientResource, string Key);

/// <summary>
/// A swept page: how to reach its state, its primary action, the key elements that must never overlap, and the strings
/// that prove it speaks one language.
/// </summary>
public sealed record SweptPage(
    string Name,
    bool SignedIn,
    bool Admin,
    string PrimaryAction,
    bool PrimaryInViewport,
    bool HasCode,
    IReadOnlyList<string> KeyElements,
    IReadOnlyList<LanguageCheck> Language)
{
    /// <summary>Whether the page shows keyboard shortcut hints (spec 9.1) to a fine pointer.</summary>
    public bool HasShortcutHints { get; init; }

    /// <summary>The fewest controls the accessibility tree must hold, so the name check cannot pass on an empty page.</summary>
    public int MinimumControls { get; init; } = 5;
}

/// <summary>Everything one rendering measured; each test asserts its own part.</summary>
public sealed record PageProbe(
    ProbeKey Key,
    int ScrollWidth,
    int InnerWidth,
    string PrimaryProblem,
    IReadOnlyList<string> Overlaps,
    IReadOnlyList<string> BrokenWords,
    int CodeElements,
    double MinCodeFontPx,
    int Targets,
    IReadOnlyList<string> UndersizedTargets,
    bool FinePointer,
    int KeyboardHints,
    int KeyboardHintsShown,
    int Controls,
    IReadOnlyList<string> UnnamedControls,
    string DocumentLanguage,
    string? IslandCulture,
    IReadOnlyList<(LanguageCheck Check, string? Actual)> LanguageValues,
    IReadOnlyList<string> CspViolations);

/// <summary>
/// The host for the island sweep and a cache of page probes: each rendering (page, width, theme, pointer, culture) is
/// prepared once and measured once, and every test reads its own assertion from that measurement, so the matrix costs
/// one island boot per rendering instead of one per assertion.
/// </summary>
public sealed class IslandSweepFixture(PostgresFixture postgres, BrowserFixture browser) : IAsyncLifetime
{
    public const string QuestionWithCodeId = "01KBZ3Q8N9V2K3M4P5R6S7T8WC";
    public const string OutputQuestionId = "01KBZ3Q8N9V2K3M4P5R6S7T8WA";
    public const int MinimumTarget = 44;

    private const string ReadyIsland = ".island-root[data-island-ready='true']";

    private static readonly TimeSpan IslandTimeout = TimeSpan.FromSeconds(60);
    private static readonly string ScreenshotDirectory = Path.Combine(ContentWorkspace.RepositoryRoot(), "TestResults", "screenshots");

    private static readonly LanguageCheck[] Chrome =
    [
        new(".skip-link", null, false, "Nav.SkipToContent"),
        new(".lang-switch", "aria-label", false, "Language.Label"),
        new("#theme-toggle", "aria-label", false, "Theme.Toggle"),
    ];

    private static readonly LanguageCheck SignOut = new(".account__logout .account__button", null, false, "Account.SignOut");
    private static readonly LanguageCheck SignIn = new("a.account__button", null, false, "Account.SignIn");

    private static readonly string[] Header = [".site-header .brand", ".site-header__tools"];
    private static readonly string[] Question = [".qcard__title", ".qcard .editor", ".opts--pick"];

    public static readonly IReadOnlyDictionary<string, SweptPage> Pages = new Dictionary<string, SweptPage>(StringComparer.Ordinal)
    {
        ["practice"] = new(
            "practice", false, false, ".practice__answer", true, true,
            [.. Header, .. Question, ".practice__answer"],
            [.. Chrome, SignIn, new(".practice__answer", null, true, "Practice.Answer"), new("pre.code", "aria-label", true, "Question.Code")])
        { HasShortcutHints = true },
        ["practice-feedback"] = new(
            "practice-feedback", false, false, ".practice__next", true, true,
            [.. Header, .. Question, ".feedback", ".practice__next"],
            [.. Chrome, SignIn, new(".feedback", "aria-label", true, "Feedback.Label"), new(".feedback__verdict", null, true, "Feedback.Expected")])
        { HasShortcutHints = true },
        // The signed-in answered state: the feedback terminal plus the rate and report bar, with the report form open.
        ["practice-feedback-signed-in"] = new(
            "practice-feedback-signed-in", true, false, ".practice__next", true, true,
            [.. Header, .. Question, ".feedback", ".feedback-bar", ".practice__next"],
            [.. Chrome, SignOut, new(".feedback", "aria-label", true, "Feedback.Label"), new(".feedback-bar", "aria-label", true, "QuestionFeedback.Label"), new(".feedback-bar__summary", null, true, "QuestionFeedback.Report")])
        { HasShortcutHints = true },
        // A signed-in reader of the static question page: the rate and report forms, with the report form open.
        ["question-feedback"] = new(
            "question-feedback", true, false, ".feedback-bar__send", false, true,
            [.. Header, ".question__title", ".question .editor", "#feedback"],
            [.. Chrome, SignOut, new(".feedback-forms__heading", null, false, "QuestionFeedback.Label"), new(".feedback-bar__summary", null, false, "QuestionFeedback.Report")])
        { MinimumControls = 8 },
        ["mock"] = new(
            "mock", false, false, ".mock__finish", true, false,
            [.. Header, .. Question, ".jump", ".mock__finish"],
            [.. Chrome, SignIn, new(".mock__finish", null, true, "Mock.Finish")])
        { HasShortcutHints = true },
        ["mock-summary"] = new(
            "mock-summary", false, false, ".mock-summary__new", false, false,
            [.. Header, "#mock-summary-heading", ".mock-summary__table-wrap", ".mock-summary__list", ".mock-summary__new"],
            [.. Chrome, SignIn, new(".mock-summary__new", null, true, "Mock.NewMock")]),
        ["review"] = new(
            "review", true, false, ".recall__show", true, true,
            [.. Header, ".qcard__title", ".qcard .editor", ".recall"],
            [.. Chrome, SignOut, new(".recall__show", null, true, "Review.Recall.Show")])
        { HasShortcutHints = true },
        ["profile"] = new(
            "profile", true, false, ".profile__due a", true, false,
            [.. Header, ".profile__heading", ".profile__stats", ".profile__progress"],
            [.. Chrome, SignOut, new(".profile__heading", null, true, "Profile.Heading")]),
        ["signin"] = new(
            "signin", false, false, ".signin a.btn--primary", true, false,
            [.. Header, ".signin h1", ".signin a.btn--primary"],
            [.. Chrome, new(".signin h1", null, false, "SignIn.Heading")]),
        ["admin"] = new(
            "admin", false, true, ".admin__search button[type=submit]", false, false,
            [.. Header, ".admin h1", ".admin__search", ".admin .table-scroll"],
            [.. Chrome, SignOut, new(".admin h1", null, false, "Admin.Heading"), new(".admin__search button[type=submit]", null, false, "Admin.Users.Search")]),

        // The 429 page of a rate-limited catalog search: a bare document without the site chrome (CatalogSearchRateLimit).
        ["search-429"] = new(
            "search-429", false, false, "main a.btn", true, false,
            ["main h1", "main a.btn"],
            [new("main h1", null, false, "Search.RateLimited.Title"), new("main a.btn", null, false, "Search.RateLimited.Back")])
        { MinimumControls = 1 },
    };

    private static long _nextTelegramId = 990_700_000L;

    private readonly ConcurrentDictionary<ProbeKey, Lazy<Task<PageProbe>>> _probes = new();

    // The sweep searches the catalog only to render the 429 page, so its host allows one search a minute.
    public AppHostFixture Host { get; } = new(postgres, browser, new Dictionary<string, string?> { ["App:RateLimits:SearchPerMinute"] = "1" });

    public async ValueTask InitializeAsync()
    {
        await Host.InitializeAsync();
        await SeedAdminFeedbackAsync();
    }

    // The admin page shows its report and dislike tables only when there is something in them: a long unbroken word and
    // several lines are the comment shapes most likely to push the table sideways.
    private Task SeedAdminFeedbackAsync() => Task.WhenAll(
        Host.SeedFeedbackAsync(
            990_710_001L, "Sweep Reporter With A Long Display Name That Has To Wrap", QuestionWithCodeId, "ru", ReportReason.WrongAnswer,
            "First line of a comment\n" + new string('w', 120) + "\nLast line", dislike: true),
        Host.SeedFeedbackAsync(990_710_002L, "Sweep Reporter", OutputQuestionId, "en", ReportReason.BrokenLink, null, dislike: false));

    public ValueTask DisposeAsync() => Host.DisposeAsync();

    /// <summary>The measurement of <paramref name="key"/>, prepared on first use and shared by every later test.</summary>
    public Task<PageProbe> ProbeAsync(ProbeKey key) =>
        _probes.GetOrAdd(key, k => new Lazy<Task<PageProbe>>(() => RunNamedAsync(k))).Value;

    /// <summary>A tab of <paramref name="context"/> showing <paramref name="page"/> in its swept state, signed in as the page needs.</summary>
    public async Task<IPage> OpenAsync(IBrowserContext context, string page, string culture)
    {
        ArgumentNullException.ThrowIfNull(context);
        SweptPage swept = Pages[page];
        await AuthenticateAsync(context, swept);
        IPage tab = await Host.NewPageAsync(context);
        await PrepareAsync(tab, swept, culture);
        return tab;
    }

    /// <summary>A browser context sized and configured for <paramref name="key"/>, with the theme stored before any script runs.</summary>
    public async Task<IBrowserContext> NewContextAsync(int width, string theme, PointerKind device, string culture)
    {
        IBrowserContext context = await Host.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = Host.BaseAddress.ToString(),
            Locale = culture == "ru" ? "ru-RU" : "en-US",
            ViewportSize = new ViewportSize { Width = width, Height = width < 640 ? 812 : 900 },
            HasTouch = device == PointerKind.Coarse,
            IsMobile = device == PointerKind.Coarse,
        });
        if (device == PointerKind.FineWithTouch)
        {
            await context.AddInitScriptAsync("Object.defineProperty(Navigator.prototype, 'maxTouchPoints', { get: () => 5 }); window.ontouchstart = null;");
        }

        await context.AddInitScriptAsync($"try {{ localStorage.setItem('theme', '{theme}'); }} catch (e) {{ }}");
        return context;
    }

    /// <summary>The value of <paramref name="key"/> in the server's or the WebAssembly client's resources for <paramref name="culture"/>.</summary>
    /// <remarks>
    /// Read from the compiled satellite assemblies the app itself loads, without culture fallback, so a key missing in one
    /// culture fails here instead of silently matching the other language.
    /// </remarks>
    public static string Resource(bool clientResource, string culture, string key)
    {
        ResourceSet set = ResourceSets.GetOrAdd((clientResource, culture), LoadResourceSet);
        return set.GetString(key, ignoreCase: false)
            ?? throw new InvalidOperationException($"{(clientResource ? "ClientResource" : "SharedResource")}.{culture} has no '{key}'.");
    }

    private static readonly ConcurrentDictionary<(bool Client, string Culture), ResourceSet> ResourceSets = new();

    private static ResourceSet LoadResourceSet((bool Client, string Culture) which)
    {
        Assembly main = which.Client ? typeof(Trainer.Web.Client.Localization.ClientResource).Assembly : typeof(SharedResource).Assembly;
        Assembly satellite = main.GetSatelliteAssembly(CultureInfo.GetCultureInfo(which.Culture));
        string suffix = (which.Client ? ".ClientResource." : ".SharedResource.") + which.Culture + ".resources";
        string name = satellite.GetManifestResourceNames().Single(n => n.EndsWith(suffix, StringComparison.Ordinal));
        return new ResourceSet(satellite.GetManifestResourceStream(name)!);
    }

    /// <summary>Opens Practice on the question with code (single choice), waiting for the island to show it.</summary>
    public static async Task<JsonElement> OpenPracticeQuestionAsync(IPage tab, string culture)
    {
        ResponseTap tap = await ResponseTap.AttachAsync(tab, "**/api/sessions/practice");
        await tab.GotoAsync($"/{culture}/dotnet/practice?first={QuestionWithCodeId}");
        JsonElement session = await tap.WaitForAsync(1);
        await tab.Locator(".qcard").WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        return session;
    }

    /// <summary>Picks a wrong option of the question with code and answers; waits for the outbox write and the feedback.</summary>
    public static async Task AnswerWronglyAsync(IPage tab, JsonElement session)
    {
        string wrong = session.GetProperty("questions").EnumerateArray()
            .First(q => q.GetProperty("id").GetString() == QuestionWithCodeId)
            .GetProperty("options").EnumerateArray()
            .First(o => !o.GetProperty("correct").GetBoolean())
            .GetProperty("id").GetString()!;
        await tab.Locator($".opt--pick:has(input[value='{wrong}'])").ClickAsync();
        await tab.Locator(".practice__answer").ClickAsync();
        await tab.Locator(".qs[data-answers-saved='1']").WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        await tab.Locator(".feedback").WaitForAsync();
    }

    private static float Wait => (float)IslandTimeout.TotalMilliseconds;

    // A failed preparation is cached like a result, so every test reading this rendering reports the same failure; the
    // key in the message says which rendering it was.
    private async Task<PageProbe> RunNamedAsync(ProbeKey key)
    {
        try
        {
            return await RunAsync(key);
        }
        catch (Exception e) when (e is not Xunit.Sdk.XunitException)
        {
            throw new InvalidOperationException($"Preparing {key} failed: {e.Message}", e);
        }
    }

    private async Task<PageProbe> RunAsync(ProbeKey key)
    {
        SweptPage page = Pages[key.Page];
        await using IBrowserContext context = await NewContextAsync(key.Width, key.Theme, key.Device, key.Culture);
        await AuthenticateAsync(context, page);
        IPage tab = await Host.NewPageAsync(context);
        await PrepareAsync(tab, page, key.Culture);
        await tab.EvaluateAsync("document.fonts.ready");

        // Measured before anything scrolls or the full-page screenshot resizes the viewport.
        int scrollWidth = await tab.EvaluateAsync<int>("document.documentElement.scrollWidth");
        int innerWidth = await tab.EvaluateAsync<int>("window.innerWidth");
        string primaryProblem = await PrimaryProblemAsync(tab, page, key.Width);
        string[] brokenWords = await tab.EvaluateAsync<string[]>(BrokenWordsScript);
        double[] codeFonts = await tab.EvaluateAsync<double[]>(
            "() => [...document.querySelectorAll('.code-line, pre.code')].filter(e => e.getClientRects().length > 0).map(e => parseFloat(getComputedStyle(e).fontSize))");
        string[] overlaps = await tab.EvaluateAsync<string[]>(OverlapScript, page.KeyElements.ToArray());

        if (key.Device == ProbeKey.NaturalPointer(key.Width) && key.Culture == "en")
        {
            await tab.EvaluateAsync("() => window.scrollTo(0, 0)");
            Directory.CreateDirectory(ScreenshotDirectory);
            await tab.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(ScreenshotDirectory, $"island-{key.Page}-{key.Width}-{key.Theme}.png"),
                FullPage = true,
            });
        }

        int[] hints = await tab.EvaluateAsync<int[]>(
            "() => { const all = [...document.querySelectorAll('.kbd-hint')]; return [all.length, all.filter(e => getComputedStyle(e).display !== 'none').length]; }");

        // Drawers (the topic sidebar, the catalog-style filters) hold targets too; their links have no box while closed.
        await tab.EvaluateAsync("() => document.querySelectorAll('.island-root details:not([open])').forEach(d => { d.open = true; })");
        string[] targets = await tab.EvaluateAsync<string[]>(TargetsScript, MinimumTarget);
        (int controls, string[] unnamed) = await UnnamedControlsAsync(context, tab);
        List<(LanguageCheck, string?)> language = [];
        foreach (LanguageCheck check in page.Language)
        {
            ILocator element = tab.Locator(check.Selector).First;
            string? actual = await element.CountAsync() == 0 ? null
                : check.Attribute is { } attribute ? await element.GetAttributeAsync(attribute)
                : await element.TextContentAsync();
            language.Add((check, actual));
        }

        string documentLanguage = await tab.EvaluateAsync<string>("() => document.documentElement.lang");
        string? islandCulture = await tab.EvaluateAsync<string?>("() => document.querySelector('.island-root')?.getAttribute('data-island-culture') ?? null");
        await tab.WaitForLoadStateAsync(LoadState.NetworkIdle);
        string[] violations = CspViolations(tab);

        return new PageProbe(
            key,
            scrollWidth,
            innerWidth,
            primaryProblem,
            overlaps,
            brokenWords,
            codeFonts.Length,
            codeFonts.Length == 0 ? double.NaN : codeFonts.Min(),
            int.Parse(targets[0], CultureInfo.InvariantCulture),
            targets[1..],
            await tab.EvaluateAsync<bool>("() => matchMedia('(pointer: fine)').matches"),
            hints[0],
            hints[1],
            controls,
            unnamed,
            documentLanguage,
            islandCulture,
            language,
            violations);
    }

    private string[] CspViolations(IPage tab)
    {
        try
        {
            Host.AssertNoCspViolations(tab);
            return [];
        }
        catch (Xunit.Sdk.XunitException e)
        {
            return [e.Message];
        }
    }

    private async Task AuthenticateAsync(IBrowserContext context, SweptPage page)
    {
        if (page.Admin)
        {
            await context.AddCookiesAsync([new Cookie { Name = AuthCookieNames.Session, Value = await Host.IssueAdminSessionAsync(), Url = Host.BaseAddress.ToString() }]);
        }
        else if (page.SignedIn)
        {
            long telegramId = Interlocked.Increment(ref _nextTelegramId);
            await Host.SignInAsync(context, new FakeTelegramIdentity(telegramId, "Sweep Tester", "sweep_tester"));

            // A question answered wrongly two days ago is due: Review has a question and the profile a link to it.
            await Host.RecordAnswersAsync(
                telegramId,
                (QuestionWithCodeId, Grade.DidntKnow, TimeSpan.FromDays(2)),
                (OutputQuestionId, Grade.Knew, TimeSpan.FromHours(1)));
        }
    }

    private static async Task PrepareAsync(IPage tab, SweptPage page, string culture)
    {
        switch (page.Name)
        {
            case "practice":
                await OpenPracticeQuestionAsync(tab, culture);
                break;
            case "practice-feedback":
                await AnswerWronglyAsync(tab, await OpenPracticeQuestionAsync(tab, culture));
                break;
            case "practice-feedback-signed-in":
                await AnswerWronglyAsync(tab, await OpenPracticeQuestionAsync(tab, culture));
                await tab.Locator(".feedback-bar__summary").ClickAsync();
                await tab.Locator(".feedback-bar__reasons").WaitForAsync();
                break;
            case "question-feedback":
                Assert.Equal(200, (await tab.GotoAsync($"/{culture}/dotnet/questions/closure-for-loop"))?.Status);
                await tab.Locator(".feedback-bar__summary").ClickAsync();
                await tab.Locator(".feedback-bar__reasons").WaitForAsync();
                break;
            case "mock":
                await StartMockAsync(tab, culture);
                break;
            case "mock-summary":
                await StartMockAsync(tab, culture);
                await FinishMockAsync(tab);
                break;
            case "review":
                await tab.GotoAsync($"/{culture}/dotnet/review");
                await tab.Locator(".recall__show").WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
                break;
            case "profile":
                await tab.GotoAsync($"/{culture}/dotnet/profile");
                await tab.Locator(".profile__due a").WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
                break;
            case "signin":
                Assert.Equal(200, (await tab.GotoAsync($"/{culture}/signin"))?.Status);
                break;
            case "admin":
                Assert.Equal(200, (await tab.GotoAsync($"/{culture}/admin"))?.Status);
                Assert.True(await tab.Locator(".admin-table--reports").CountAsync() > 0, "The admin sweep has no report rows to measure.");
                break;
            case "search-429":
                await OpenRateLimitedSearchAsync(tab, culture);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(page), page.Name, "No preparation for this page.");
        }
    }

    // One search a minute per address: the first request of a window may still pass, the next one is refused.
    private static async Task OpenRateLimitedSearchAsync(IPage tab, string culture)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            IResponse? response = await tab.GotoAsync($"/{culture}/dotnet/questions?q=ConfigureAwait");
            if (response?.Status == 429)
            {
                return;
            }
        }

        Assert.Fail("The catalog search was never rate limited.");
    }

    private static async Task StartMockAsync(IPage tab, string culture)
    {
        await tab.GotoAsync($"/{culture}/dotnet/mock");
        await tab.Locator(ReadyIsland).WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        await tab.Locator(".mock-setup").WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        await tab.Locator(".mock-setup input[name='mock-level'][value='middle']").CheckAsync();
        ILocator count = tab.Locator(".mock-setup input[name='count']");
        await count.FillAsync("5");
        await count.DispatchEventAsync("change");
        await tab.Locator(".mock-setup__start").ClickAsync();
        await WaitForPhaseAsync(tab, "prepared", "answering");
        if (await tab.Locator(".mock").GetAttributeAsync("data-mock-phase") == "prepared")
        {
            await tab.Locator(".mock-warn__anyway").ClickAsync();
        }

        await WaitForPhaseAsync(tab, "answering");
        await tab.Locator(".qcard").WaitForAsync();
    }

    // Finishes unanswered and skips every self-assessment: the summary then lists all five questions.
    private static async Task FinishMockAsync(IPage tab)
    {
        await tab.Locator(".mock__finish").ClickAsync();
        await WaitForPhaseAsync(tab, "assessment", "summary");
        while (await tab.Locator(".mock").GetAttributeAsync("data-mock-phase") == "assessment")
        {
            await tab.Locator(".mock-assess__item:has(.self__grades)").First.Locator(".self__grades .btn--quiet").ClickAsync();
            await WaitForPhaseAsync(tab, "assessment", "summary");
            await tab.WaitForFunctionAsync(
                "() => !document.querySelector('.mock-assess [role=status]')",
                null,
                new PageWaitForFunctionOptions { Timeout = Wait });
        }

        await tab.Locator(".mock-summary__new").WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
    }

    private static Task WaitForPhaseAsync(IPage tab, params string[] phases) =>
        tab.Locator(string.Join(", ", phases.Select(p => $".mock[data-mock-phase='{p}']")))
            .First.WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });

    // The primary action must be rendered and reachable: on the question screens of a phone it is inside the first
    // viewport (the sticky bottom bar of spec 9.1, below 640 px); everywhere, once scrolled into view, a click at its
    // centre lands on it.
    private static async Task<string> PrimaryProblemAsync(IPage tab, SweptPage page, int width) =>
        await tab.EvaluateAsync<string>(
            """
            ([selector, inViewport]) => {
              const e = document.querySelector(selector);
              if (!e) return `${selector} is missing`;
              let r = e.getBoundingClientRect();
              if (r.width === 0 || r.height === 0 || !e.checkVisibility({ checkOpacity: true, visibilityProperty: true })) return `${selector} is not visible`;
              if (inViewport && (r.top < 0 || r.left < 0 || r.bottom > window.innerHeight + 0.5 || r.right > window.innerWidth + 0.5)) {
                return `${selector} is outside the first viewport: ${Math.round(r.left)},${Math.round(r.top)} ${Math.round(r.width)}x${Math.round(r.height)} in ${window.innerWidth}x${window.innerHeight}`;
              }
              e.scrollIntoView({ block: 'center', inline: 'center' });
              r = e.getBoundingClientRect();
              const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
              window.scrollTo(0, 0);
              if (!hit || !e.contains(hit)) return `${selector} is covered by ${hit ? hit.tagName.toLowerCase() + '.' + hit.className : 'nothing'}`;
              return '';
            }
            """,
            new object[] { page.PrimaryAction, page.PrimaryInViewport && width < 640 });

    // Rectangles in document coordinates, with the page scrolled to the end so that a sticky bar sits where the flow puts
    // it (while stuck it covers content scrolling under it by design). Nested pairs are not overlaps.
    private const string OverlapScript =
        """
        (selectors) => {
          window.scrollTo(0, document.documentElement.scrollHeight);
          const items = [];
          for (const s of selectors) {
            for (const e of document.querySelectorAll(s)) {
              const r = e.getBoundingClientRect();
              if (r.width === 0 || r.height === 0) continue;
              items.push({ s, e, l: r.left + scrollX, t: r.top + scrollY, r: r.right + scrollX, b: r.bottom + scrollY });
            }
          }
          window.scrollTo(0, 0);
          const out = [];
          for (let i = 0; i < items.length; i++) {
            for (let j = i + 1; j < items.length; j++) {
              const a = items[i], b = items[j];
              if (a.e === b.e || a.e.contains(b.e) || b.e.contains(a.e)) continue;
              const w = Math.min(a.r, b.r) - Math.max(a.l, b.l);
              const h = Math.min(a.b, b.b) - Math.max(a.t, b.t);
              if (w > 1 && h > 1) out.push(`${a.s} and ${b.s} overlap by ${Math.round(w)}x${Math.round(h)}`);
            }
          }
          return out;
        }
        """;

    // A word whose characters land on more than one line was broken inside itself.
    private const string BrokenWordsScript =
        """
        () => {
          const out = [];
          for (const el of document.querySelectorAll('.site-header .brand, .site-header .lang-switch a, .site-header .account__button, .site-header .account__name')) {
            const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT);
            for (let n = walker.nextNode(); n; n = walker.nextNode()) {
              for (const m of n.data.matchAll(/\S+/g)) {
                const range = document.createRange();
                range.setStart(n, m.index);
                range.setEnd(n, m.index + m[0].length);
                const lines = new Set([...range.getClientRects()].filter(r => r.width > 0).map(r => Math.round(r.top)));
                if (lines.size > 1) out.push(m[0]);
              }
            }
          }
          return out;
        }
        """;

    // First item: how many targets were measured; then one line per target below the minimum. A radio or checkbox is
    // measured through its label, which is the target the user hits.
    private const string TargetsScript =
        """
        (min) => {
          const selector = [
            '.island-root a[href]', '.island-root button', '.island-root summary', '.island-root textarea', '.island-root select',
            '.island-root input:not([type=radio]):not([type=checkbox]):not([type=hidden])', '.island-root label:has(> input)',
            '.site-header a[href]', '.site-header button',
            '.admin a[href]', '.admin button', '.admin select', '.admin input:not([type=hidden])',
            '.signin a[href]', '.signin button', 'main a[href]', 'main button',
            '#feedback a[href]', '#feedback button', '#feedback summary', '#feedback textarea', '#feedback label:has(> input)'
          ].join(', ');
          const targets = [...document.querySelectorAll(selector)].filter(e => e.getClientRects().length > 0);
          const small = targets
            .map(e => ({ e, r: e.getBoundingClientRect() }))
            .filter(x => x.r.width < min || x.r.height < min)
            .map(x => `${x.e.tagName.toLowerCase()}.${x.e.className} "${(x.e.textContent || x.e.getAttribute('aria-label') || x.e.name || '').trim().slice(0, 30)}" ${Math.round(x.r.width)}x${Math.round(x.r.height)}`);
          return [String(targets.length), ...small];
        }
        """;

    private static readonly HashSet<string> InteractiveRoles = new(StringComparer.Ordinal)
    {
        "button", "link", "textbox", "searchbox", "checkbox", "radio", "combobox", "listbox", "spinbutton", "slider", "switch",
        "tab", "menuitem", "menuitemcheckbox", "menuitemradio", "option", "treeitem", "DisclosureTriangle",
    };

    // Chromium's computed accessibility tree: every control, and every element in the tab order, must have a name.
    // Elements focusable only by script (tabindex="-1", such as headings that receive focus) are not controls.
    private static async Task<(int Controls, string[] Unnamed)> UnnamedControlsAsync(IBrowserContext context, IPage tab)
    {
        ICDPSession cdp = await context.NewCDPSessionAsync(tab);
        JsonElement tree = (await cdp.SendAsync("Accessibility.getFullAXTree"))!.Value;
        List<string> unnamed = [];
        int controls = 0;
        foreach (JsonElement node in tree.GetProperty("nodes").EnumerateArray())
        {
            if (node.TryGetProperty("ignored", out JsonElement ignored) && ignored.GetBoolean())
            {
                continue;
            }

            string role = node.TryGetProperty("role", out JsonElement r) && r.TryGetProperty("value", out JsonElement rv) ? rv.GetString() ?? "" : "";
            string name = node.TryGetProperty("name", out JsonElement n) && n.TryGetProperty("value", out JsonElement nv) ? nv.GetString() ?? "" : "";
            bool focusable = node.TryGetProperty("properties", out JsonElement properties)
                && properties.EnumerateArray().Any(p => p.GetProperty("name").GetString() == "focusable" && p.GetProperty("value").GetProperty("value").GetBoolean());
            if (role == "RootWebArea" || !(InteractiveRoles.Contains(role) || focusable))
            {
                continue;
            }

            controls++;
            if (!string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            string element = "?";
            if (node.TryGetProperty("backendDOMNodeId", out JsonElement backend))
            {
                JsonElement described = (await cdp.SendAsync("DOM.describeNode", new Dictionary<string, object> { ["backendNodeId"] = backend.GetInt32() }))!.Value;
                JsonElement domNode = described.GetProperty("node");
                string[] attributes = domNode.TryGetProperty("attributes", out JsonElement a) ? [.. a.EnumerateArray().Select(x => x.GetString() ?? "")] : [];
                int tabIndex = Array.IndexOf(attributes, "tabindex");
                if (!InteractiveRoles.Contains(role) && tabIndex >= 0 && tabIndex % 2 == 0 && attributes[tabIndex + 1] == "-1")
                {
                    continue;
                }

                int classIndex = Array.IndexOf(attributes, "class");
                element = domNode.GetProperty("localName").GetString() + (classIndex >= 0 && classIndex % 2 == 0 ? "." + attributes[classIndex + 1] : "");
            }

            unnamed.Add($"{role} {element}");
        }

        await cdp.DetachAsync();
        return (controls, [.. unnamed]);
    }
}
