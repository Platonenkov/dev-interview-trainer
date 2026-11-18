using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Trainer.Infrastructure.Persistence;
using Trainer.TestSupport;

namespace Trainer.Web.E2E;

/// <summary>
/// Practice in a real browser: every question kind, the keyboard, the outbox and its sync, the island states, the
/// remembered filters, the layout at the supported widths and the entry points from public pages. Every test checks
/// that no document it showed reported a CSP violation.
/// </summary>
[Trait("Category", "E2E")]
public sealed class PracticeFlowTests(AppHostFixture host) : IClassFixture<AppHostFixture>
{
    private const string PracticePath = "/en/dotnet/practice";
    private const string ReadyIsland = ".island-root[data-island-ready='true']";
    private const string AnonymousOutbox = "dit.outbox.v1.anon";

    private const string ClosureId = "01KBZ3Q8N9V2K3M4P5R6S7T8WA";
    private const string OpenId = "01KBZ3Q8N9V2K3M4P5R6S7T8WB";
    private const string SingleId = "01KBZ3Q8N9V2K3M4P5R6S7T8WC";
    private const string FindBugId = "01KBZ3Q8N9V2K3M4P5R6S7T8WE";

    private const int MinimumTarget = 44;

    private static readonly TimeSpan IslandTimeout = TimeSpan.FromSeconds(60);
    private static readonly string ScreenshotDirectory = Path.Combine(ContentWorkspace.RepositoryRoot(), "TestResults", "screenshots");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<IBrowserContext> NewContextAsync(
        int width = 1280, int height = 900, string theme = "dark", string locale = "en-US", bool touch = false, bool mobile = false)
    {
        IBrowserContext context = await host.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString(),
            Locale = locale,
            ViewportSize = new ViewportSize { Width = width, Height = height },
            HasTouch = touch,
            IsMobile = mobile,
        });
        await context.AddInitScriptAsync($"try {{ localStorage.setItem('dit.theme', '{theme}'); }} catch (e) {{ }}");
        return context;
    }

    private static async Task WaitForIslandAsync(IPage tab) =>
        await tab.Locator(ReadyIsland).WaitForAsync(new LocatorWaitForOptions { Timeout = (float)IslandTimeout.TotalMilliseconds });

    // Session bodies are read through a route: Chromium may drop the body of a fetch response before the test reads it.
    private static readonly ConditionalWeakTable<IPage, SessionTap> Taps = [];

    private static async Task<SessionTap> TapAsync(IPage tab, Action<JsonNode>? transform = null)
    {
        if (Taps.TryGetValue(tab, out SessionTap? existing))
        {
            return existing;
        }

        SessionTap tap = new(transform);
        Taps.Add(tab, tap);
        await tab.RouteAsync("**/api/sessions/practice", tap.HandleAsync);
        return tap;
    }

    /// <summary>Opens a Practice URL with <c>first=</c>, which starts a session at once; returns the session JSON.</summary>
    private static async Task<JsonElement> OpenFirstAsync(IPage tab, string questionId, string query = "")
    {
        SessionTap tap = await TapAsync(tab);
        int before = tap.Count;
        await tab.GotoAsync($"{PracticePath}?first={questionId}{query}");
        JsonElement json = await tap.WaitForAsync(before + 1);
        await tab.Locator(".qcard").WaitForAsync(new LocatorWaitForOptions { Timeout = (float)IslandTimeout.TotalMilliseconds });
        return json;
    }

    /// <summary>Opens the filter form, optionally sets the count, presses Start and returns the session JSON.</summary>
    private static async Task<JsonElement> StartAsync(IPage tab, string path = PracticePath, int? count = null)
    {
        SessionTap tap = await TapAsync(tab);
        await tab.GotoAsync(path);
        await WaitForIslandAsync(tab);
        if (count is { } n)
        {
            await tab.Locator(".pf input[name='count']").FillAsync(n.ToString(CultureInfo.InvariantCulture));
        }

        return await PressAsync(tab, tap, ".pf__start");
    }

    private static async Task<JsonElement> PressAsync(IPage tab, SessionTap tap, string selector)
    {
        int before = tap.Count;
        await tab.Locator(selector).ClickAsync();
        return await tap.WaitForAsync(before + 1);
    }

    private static JsonElement Question(JsonElement session, string id) =>
        session.GetProperty("questions").EnumerateArray().First(q => q.GetProperty("id").GetString() == id);

    // The island counts the answers whose outbox write has returned (data-answers-saved); storage checks wait on it,
    // never on the feedback appearing.
    private static async Task<int> SavedAsync(IPage tab) =>
        int.Parse(await tab.Locator(".qs").GetAttributeAsync("data-answers-saved") ?? "0", CultureInfo.InvariantCulture);

    private static async Task WaitForSavedAsync(IPage tab, int count) =>
        await tab.Locator($".qs[data-answers-saved='{count}']").WaitForAsync();

    private static async Task SubmitAsync(IPage tab)
    {
        int saved = await SavedAsync(tab);
        await tab.Locator(".practice__answer").ClickAsync();
        await WaitForSavedAsync(tab, saved + 1);
        await tab.Locator(".feedback").WaitForAsync();
    }

    // Answers whatever question is on screen, with the first option, the first line, some text or "knew".
    private static async Task AnswerShownAsync(IPage tab)
    {
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
            int saved = await SavedAsync(tab);
            await tab.Locator(".self > button.btn").ClickAsync();
            await tab.Locator(".self__grade--knew").ClickAsync();
            await WaitForSavedAsync(tab, saved + 1);
            await tab.Locator(".feedback").WaitForAsync();
            return;
        }

        await SubmitAsync(tab);
    }

    private static async Task NextAsync(IPage tab) => await tab.Locator(".practice__next").ClickAsync();

    private static async Task<JsonElement?> StoredAsync(IPage tab, string key)
    {
        string? raw = await tab.EvaluateAsync<string?>("k => localStorage.getItem(k)", key);
        return raw is null ? null : JsonDocument.Parse(raw).RootElement.Clone();
    }

    private static async Task<int> StoredCountAsync(IPage tab, string key) =>
        await StoredAsync(tab, key) is { } document ? document.GetProperty("answers").GetArrayLength() : 0;

    private async Task<int> ServerAnswersAsync(long telegramUserId)
    {
        await using TrainerDbContext db = PostgresFixture.CreateContext(host.ConnectionString);
        return await db.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM answers a JOIN users u ON u.id = a.user_id WHERE u.telegram_user_id = {telegramUserId}")
            .SingleAsync(Ct);
    }

    private async Task WaitForServerAnswersAsync(long telegramUserId, int expected, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        int last;
        while ((last = await ServerAnswersAsync(telegramUserId)) != expected)
        {
            Assert.True(DateTime.UtcNow < deadline, $"Expected {expected} answer rows for {telegramUserId}, found {last}.");
            await Task.Delay(250, Ct);
        }
    }

    private static string Resource(string culture, string key)
    {
        string path = Path.Combine(ContentWorkspace.RepositoryRoot(), "src", "Trainer.Web.Client", "Localization", $"ClientResource.{culture}.resx");
        return XDocument.Load(path).Descendants("data").Single(d => (string?)d.Attribute("name") == key).Element("value")!.Value;
    }

    // Turns the closure question of a session response into a typed output question (the fixture has no such question).
    private static Task<SessionTap> RouteClosureAsTypedAsync(IPage tab) => TapAsync(tab, session =>
    {
        foreach (JsonNode? question in session["questions"]!.AsArray())
        {
            if ((string?)question!["id"] == ClosureId)
            {
                question["options"] = new JsonArray();
            }
        }
    });

    /// <summary>Records every session response (and its request) a tab receives, optionally rewriting the body.</summary>
    private sealed class SessionTap(Action<JsonNode>? transform)
    {
        private readonly List<(string Request, JsonElement Response)> _seen = [];

        public int Count
        {
            get
            {
                lock (_seen)
                {
                    return _seen.Count;
                }
            }
        }

        public string RequestBody(int index)
        {
            lock (_seen)
            {
                return _seen[index].Request;
            }
        }

        public async Task HandleAsync(IRoute route)
        {
            IAPIResponse response = await route.FetchAsync();
            string body = await response.TextAsync();
            if (transform is not null && response.Status == 200)
            {
                JsonNode node = JsonNode.Parse(body)!;
                transform(node);
                body = node.ToJsonString();
            }

            lock (_seen)
            {
                _seen.Add((route.Request.PostData ?? "", JsonDocument.Parse(body.Length == 0 ? "null" : body).RootElement.Clone()));
            }

            await route.FulfillAsync(new RouteFulfillOptions { Response = response, Body = body });
        }

        public async Task<JsonElement> WaitForAsync(int count)
        {
            DateTime deadline = DateTime.UtcNow + IslandTimeout;
            while (Count < count)
            {
                Assert.True(DateTime.UtcNow < deadline, $"Session response {count} did not arrive.");
                await Task.Delay(50);
            }

            lock (_seen)
            {
                return _seen[count - 1].Response;
            }
        }
    }

    [Fact]
    public async Task Practice_SingleWrongAnswer_ShowsTerminalFeedbackAndCorrectOption()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await host.NewPageAsync(context);
        JsonElement session = await OpenFirstAsync(tab, SingleId);
        string wrong = Question(session, SingleId).GetProperty("options").EnumerateArray()
            .First(o => !o.GetProperty("correct").GetBoolean()).GetProperty("id").GetString()!;

        // The live region exists, empty, before the answer, so screen readers announce the verdict when it appears.
        ILocator status = tab.Locator(".qs__body [role='status']");
        Assert.Equal(1, await status.CountAsync());
        Assert.Equal("", (await status.TextContentAsync())?.Trim());

        await tab.Locator($".opt--pick:has(input[value='{wrong}'])").ClickAsync();
        await SubmitAsync(tab);

        await Assertions.Expect(tab.Locator(".term.feedback .feedback__verdict--didnt-know")).ToBeVisibleAsync();
        await Assertions.Expect(tab.Locator(".pick .opt--good")).ToHaveCountAsync(1);
        await Assertions.Expect(tab.Locator(".pick .opt--bad")).ToHaveCountAsync(1);
        await Assertions.Expect(tab.Locator(".feedback .opt--good")).ToHaveCountAsync(1);
        Assert.NotEqual("", (await status.TextContentAsync())?.Trim());
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Practice_FindBug_SelectLineAndSubmit()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await host.NewPageAsync(context);
        JsonElement session = await OpenFirstAsync(tab, FindBugId);
        int[] lines = [.. Question(session, FindBugId).GetProperty("bugLineSets")[0].EnumerateArray().Select(l => l.GetInt32())];

        foreach (int line in lines)
        {
            ILocator button = tab.Locator(".code-line").Nth(line - 1);
            await button.ClickAsync();
            await Assertions.Expect(button).ToHaveAttributeAsync("aria-pressed", "true");
        }

        await SubmitAsync(tab);

        await Assertions.Expect(tab.Locator(".feedback__verdict--knew")).ToBeVisibleAsync();
        await Assertions.Expect(tab.Locator(".feedback .editor--fixed")).ToBeVisibleAsync();
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Practice_TypedOutput_Accepted()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await host.NewPageAsync(context);
        await RouteClosureAsTypedAsync(tab);
        await OpenFirstAsync(tab, ClosureId);

        await tab.Locator(".typed__input").FillAsync(" 333\n");
        await SubmitAsync(tab);

        await Assertions.Expect(tab.Locator(".feedback__verdict--knew")).ToBeVisibleAsync();
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Practice_SelfAssessed_RevealThenGrade()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await host.NewPageAsync(context);
        await OpenFirstAsync(tab, OpenId);

        await Assertions.Expect(tab.Locator(".self__grade--knew")).ToHaveCountAsync(0);
        await tab.Locator(".self__notes").FillAsync("The continuation runs on the captured context.");
        await tab.Locator(".self > button.btn").ClickAsync();
        await tab.Locator(".self__grade--partial").ClickAsync();

        await WaitForSavedAsync(tab, 1);
        await Assertions.Expect(tab.Locator(".feedback__verdict--partial")).ToBeVisibleAsync();
        JsonElement outbox = (await StoredAsync(tab, AnonymousOutbox))!.Value;
        JsonElement answer = outbox.GetProperty("answers")[0];
        Assert.Equal(OpenId, answer.GetProperty("questionId").GetString());
        Assert.Equal("partial", answer.GetProperty("grade").GetString());
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Practice_KeyboardOnly_1To4EnterN()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await host.NewPageAsync(context);
        await OpenFirstAsync(tab, SingleId);
        string title = (await tab.Locator(".qcard__title").TextContentAsync())!;
        ILocator inputs = tab.Locator(".opt--pick input");

        await tab.Keyboard.PressAsync("1");
        await Assertions.Expect(inputs.Nth(0)).ToBeCheckedAsync();
        await tab.Keyboard.PressAsync("2");
        await Assertions.Expect(inputs.Nth(1)).ToBeCheckedAsync();
        await Assertions.Expect(inputs.Nth(0)).Not.ToBeCheckedAsync();
        await tab.Keyboard.PressAsync("Enter");

        await tab.Locator(".feedback").WaitForAsync();
        // The answer moves focus to Next, so Enter or Space continues without hunting for the button.
        Assert.True(await tab.Locator(".practice__next").EvaluateAsync<bool>("e => e === document.activeElement"), "Focus did not move to Next.");
        await tab.Keyboard.PressAsync("n");

        await Assertions.Expect(tab.Locator(".qcard__title")).Not.ToHaveTextAsync(title);
        await Assertions.Expect(tab.Locator(".feedback")).ToHaveCountAsync(0);
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Practice_EnterOnFocusedHintButton_DoesNotSubmit()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await host.NewPageAsync(context);
        await OpenFirstAsync(tab, SingleId);

        await tab.Keyboard.PressAsync("1");
        await Assertions.Expect(tab.Locator(".opt--pick input").Nth(0)).ToBeCheckedAsync();
        await tab.Locator(".qcard button.btn--quiet").FocusAsync();
        await tab.Keyboard.PressAsync("Enter");

        await Assertions.Expect(tab.Locator(".qcard__hint")).ToBeVisibleAsync();
        await Assertions.Expect(tab.Locator(".feedback")).ToHaveCountAsync(0);
        await Assertions.Expect(tab.Locator(".practice__answer")).ToBeEnabledAsync();
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Practice_CtrlN_IsIgnored()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await host.NewPageAsync(context);
        await OpenFirstAsync(tab, SingleId);
        string title = (await tab.Locator(".qcard__title").TextContentAsync())!;
        await tab.Keyboard.PressAsync("1");
        await tab.Keyboard.PressAsync("Enter");
        await tab.Locator(".feedback").WaitForAsync();
        await tab.Locator("h1").ClickAsync();

        await tab.Keyboard.PressAsync("Control+n");
        await tab.Keyboard.PressAsync("Alt+n");
        await tab.WaitForTimeoutAsync(300);

        await Assertions.Expect(tab.Locator(".qcard__title")).ToHaveTextAsync(title);
        await tab.Keyboard.PressAsync("n");
        await Assertions.Expect(tab.Locator(".qcard__title")).Not.ToHaveTextAsync(title);
        host.AssertNoCspViolations(tab);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Practice_KeyboardHintsHiddenOnTouch(bool touch)
    {
        await using IBrowserContext context = await NewContextAsync(width: touch ? 375 : 1280, touch: touch, mobile: touch);
        IPage tab = await host.NewPageAsync(context);
        await OpenFirstAsync(tab, SingleId);

        ILocator hint = tab.Locator(".opt--pick .kbd-hint").First;
        Assert.Equal(!touch, await hint.IsVisibleAsync());
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Practice_AnswersStoredInAnonymousOutbox()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await host.NewPageAsync(context);
        await OpenFirstAsync(tab, SingleId);

        await AnswerShownAsync(tab);
        await NextAsync(tab);
        await AnswerShownAsync(tab);

        JsonElement outbox = (await StoredAsync(tab, AnonymousOutbox))!.Value;
        Assert.Equal(1, outbox.GetProperty("version").GetInt32());
        JsonElement[] answers = [.. outbox.GetProperty("answers").EnumerateArray()];
        Assert.Equal(2, answers.Length);
        Assert.Equal(SingleId, answers[0].GetProperty("questionId").GetString());
        string[] allowed = ["clientAnswerId", "questionId", "grade", "usedHint", "mode", "mockSessionId", "answeredAt"];
        foreach (JsonElement answer in answers)
        {
            Assert.All(answer.EnumerateObject(), p => Assert.Contains(p.Name, allowed));
            Assert.Matches("^[0-9A-HJKMNP-TV-Z]{26}$", answer.GetProperty("clientAnswerId").GetString());
            Assert.Matches("^(knew|partial|didnt-know)$", answer.GetProperty("grade").GetString());
            Assert.Equal("practice", answer.GetProperty("mode").GetString());
            Assert.Equal(JsonValueKind.False, answer.GetProperty("usedHint").ValueKind);
            Assert.True(DateTimeOffset.TryParse(answer.GetProperty("answeredAt").GetString(), CultureInfo.InvariantCulture, out _));
            Assert.True(!answer.TryGetProperty("mockSessionId", out JsonElement mock) || mock.ValueKind == JsonValueKind.Null);
        }

        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Practice_SignedIn_AnswersReachServer()
    {
        const long telegramId = 990_300_001L;
        await using IBrowserContext context = await NewContextAsync();
        await host.SignInAsync(context, new FakeTelegramIdentity(telegramId, "Practice Sync", "practice_sync"));
        IPage tab = await host.NewPageAsync(context);
        await OpenFirstAsync(tab, SingleId);

        await AnswerShownAsync(tab);

        await WaitForServerAnswersAsync(telegramId, 1, TimeSpan.FromSeconds(30));
        await tab.WaitForFunctionAsync($"() => {{ const v = localStorage.getItem('dit.outbox.v1.u.{telegramId}'); return v === null || JSON.parse(v).answers.length === 0; }}");
        Assert.Equal(0, await StoredCountAsync(tab, AnonymousOutbox));
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Practice_OfflineAnswers_SyncWhenOnline()
    {
        const long telegramId = 990_300_002L;
        await using IBrowserContext context = await NewContextAsync();
        await host.SignInAsync(context, new FakeTelegramIdentity(telegramId, "Offline Sync", "offline_sync"));
        IPage tab = await host.NewPageAsync(context);
        await OpenFirstAsync(tab, SingleId);

        await context.SetOfflineAsync(true);
        await AnswerShownAsync(tab);
        await Assertions.Expect(tab.Locator(".feedback")).ToBeVisibleAsync();
        await NextAsync(tab);
        await AnswerShownAsync(tab);
        await Assertions.Expect(tab.Locator(".feedback")).ToBeVisibleAsync();
        Assert.Equal(2, await StoredCountAsync(tab, $"dit.outbox.v1.u.{telegramId}"));
        Assert.Equal(0, await ServerAnswersAsync(telegramId));

        await context.SetOfflineAsync(false);

        // The back-off starts at 5 s and doubles; two failed attempts put the next one at most 15 s away.
        await WaitForServerAnswersAsync(telegramId, 2, TimeSpan.FromSeconds(45));
        host.AssertNoCspViolations(tab);
    }

    // data-island-ready is what docs/performance.md times as "interactive": the island has left its loading phase and
    // shows something the visitor can act on, here the first question of a ?first= link.
    [Fact]
    public async Task Ready_MarksTheFirstActionableView_NotTheFirstRender()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await host.NewPageAsync(context);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await tab.RouteAsync("**/api/sessions/practice", async route =>
        {
            held.TrySetResult();
            await release.Task;
            await route.ContinueAsync();
        });

        await tab.GotoAsync($"{PracticePath}?first={SingleId}");
        await held.Task.WaitAsync(IslandTimeout, Ct);
        await tab.Locator(".island-root").WaitForAsync();

        Assert.Null(await tab.Locator(".island-root").GetAttributeAsync("data-island-ready"));
        release.SetResult();
        await WaitForIslandAsync(tab);
        Assert.Equal(1, await tab.Locator(".qcard").CountAsync());
        host.AssertNoCspViolations(tab);
    }

    // ?perf=1 shows the phone measurement readout (docs/performance.md); without it nothing is added.
    [Fact]
    public async Task PerfReadout_OnlyWithPerfQuery()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await host.NewPageAsync(context);

        await tab.GotoAsync(PracticePath + "?perf=1");
        await WaitForIslandAsync(tab);
        ILocator readout = tab.Locator(".perf-readout");
        await Assertions.Expect(readout).ToBeVisibleAsync();
        await Assertions.Expect(readout).ToHaveAttributeAsync("role", "status");
        Assert.Matches(@"^ready \d+ ms · _framework \d+ B in \d+ files$", (await readout.InnerTextAsync()).Trim());

        await tab.GotoAsync(PracticePath);
        await WaitForIslandAsync(tab);
        // The readout is added by a MutationObserver callback in the same task as the ready mark; two animation frames
        // after the mark prove that task and its microtasks have run, so its absence is not a matter of waiting longer.
        await tab.EvaluateAsync("() => new Promise(done => requestAnimationFrame(() => requestAnimationFrame(done)))");
        Assert.Equal(0, await tab.Locator(".perf-readout").CountAsync());
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Practice_OfflineAtStart_ShowsOfflineState()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await host.NewPageAsync(context);
        await tab.GotoAsync(PracticePath);
        await WaitForIslandAsync(tab);

        await context.SetOfflineAsync(true);
        await tab.Locator(".pf__start").ClickAsync();

        await Assertions.Expect(tab.Locator(".island-state[role='alert']")).ToContainTextAsync(Resource("en", "State.Offline"));
        await context.SetOfflineAsync(false);
        await tab.Locator(".island-state .btn").ClickAsync();
        await tab.Locator(".qcard").WaitForAsync();
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Practice_NoMatches_ShowsEmptyState()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await host.NewPageAsync(context);

        JsonElement session = await StartAsync(tab, PracticePath + "?level=architect");

        Assert.Equal(0, session.GetProperty("poolSize").GetInt32());
        await Assertions.Expect(tab.Locator(".island-state[role='status']")).ToContainTextAsync(Resource("en", "State.Empty"));
        await tab.Locator(".island-state .btn").ClickAsync();
        await Assertions.Expect(tab.Locator(".pf")).ToBeVisibleAsync();
        host.AssertNoCspViolations(tab);
    }

    // Wraps window.ditStore as outbox.js defines it: outbox appends wait for window.__ditOpenGate(), or fail like a full
    // storage when failAppends is set.
    private static string OutboxGate(bool failAppends) =>
        """
        (() => {
          let open;
          const gate = new Promise(resolve => { open = resolve; });
          window.__ditOpenGate = () => open();
          let store;
          Object.defineProperty(window, 'ditStore', {
            configurable: true,
            get() { return store; },
            set(value) {
              const mutate = value.mutate;
              value.mutate = async (op) => {
                if (JSON.parse(op).op === 'append') {
                  if (FAIL) {
                    return JSON.stringify({ ok: false, persisted: false, error: 'quota', revision: null, count: 0 });
                  }
                  await gate;
                }
                return mutate(op);
              };
              store = value;
            }
          });
        })();
        """.Replace("FAIL", failAppends ? "true" : "false", StringComparison.Ordinal);

    [Fact]
    public async Task Practice_FeedbackWaitsForTheOutboxWrite()
    {
        await using IBrowserContext context = await NewContextAsync();
        await context.AddInitScriptAsync(OutboxGate(failAppends: false));
        IPage tab = await host.NewPageAsync(context);
        await OpenFirstAsync(tab, SingleId);

        await tab.Locator(".opt--pick").First.ClickAsync();
        await tab.Locator(".practice__answer").ClickAsync();
        await tab.WaitForTimeoutAsync(700);

        // The write is held: nothing graded is on screen and nothing can move on yet.
        Assert.Equal(0, await tab.Locator(".feedback").CountAsync());
        Assert.Equal(0, await tab.Locator(".practice__next").CountAsync());
        Assert.Equal(0, await SavedAsync(tab));
        await tab.Keyboard.PressAsync("n");
        Assert.Equal(1, await tab.Locator(".qcard").CountAsync());
        Assert.Null(await StoredAsync(tab, AnonymousOutbox));

        await tab.EvaluateAsync("() => window.__ditOpenGate()");

        await WaitForSavedAsync(tab, 1);
        await Assertions.Expect(tab.Locator(".feedback")).ToBeVisibleAsync();
        Assert.Equal(1, await StoredCountAsync(tab, AnonymousOutbox));
        await Assertions.Expect(tab.Locator(".practice__next")).ToBeFocusedAsync();
        host.AssertNoCspViolations(tab);
    }

    // A Web Lock that is never granted must not freeze the island: after the 5 s bound the graded state shows, the
    // answer waits in this tab's memory and a notice says saving is slow; once the write ends it reaches storage.
    [Fact]
    public async Task Practice_OutboxWriteNeverReturns_ShowsGradedStateAndSlowNotice()
    {
        await using IBrowserContext context = await NewContextAsync();
        await context.AddInitScriptAsync(OutboxGate(failAppends: false));
        IPage tab = await host.NewPageAsync(context);
        await OpenFirstAsync(tab, SingleId);
        string title = (await tab.Locator(".qcard__title").TextContentAsync())!;

        await tab.Locator(".opt--pick").First.ClickAsync();
        await tab.Locator(".practice__answer").ClickAsync();
        await tab.WaitForTimeoutAsync(1000);
        Assert.Equal(0, await tab.Locator(".feedback").CountAsync());

        await WaitForSavedAsync(tab, 1);
        await Assertions.Expect(tab.Locator(".feedback")).ToBeVisibleAsync();
        await Assertions.Expect(tab.Locator(".practice__slow")).ToHaveTextAsync(Resource("en", "Storage.Slow"));
        Assert.Null(await StoredAsync(tab, AnonymousOutbox));
        await NextAsync(tab);
        await Assertions.Expect(tab.Locator(".qcard__title")).Not.ToHaveTextAsync(title);

        await tab.EvaluateAsync("() => window.__ditOpenGate()");
        await tab.WaitForFunctionAsync($"() => {{ const v = localStorage.getItem('{AnonymousOutbox}'); return v !== null && JSON.parse(v).answers.length === 1; }}");
        await Assertions.Expect(tab.Locator(".practice__slow")).ToHaveCountAsync(0);
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Practice_SelfGrade_KeepsGradeButtonsWhileSaving()
    {
        await using IBrowserContext context = await NewContextAsync();
        await context.AddInitScriptAsync(OutboxGate(failAppends: false));
        IPage tab = await host.NewPageAsync(context);
        await OpenFirstAsync(tab, OpenId);

        await tab.Locator(".self > button.btn").ClickAsync();
        await tab.Locator(".self__grade--knew").ClickAsync();

        await Assertions.Expect(tab.Locator(".self__grade--knew")).ToBeDisabledAsync();
        await Assertions.Expect(tab.Locator(".self__saving")).ToHaveTextAsync(Resource("en", "Self.Saving"));
        Assert.Equal(0, await tab.Locator(".feedback").CountAsync());

        await tab.EvaluateAsync("() => window.__ditOpenGate()");
        await WaitForSavedAsync(tab, 1);
        await Assertions.Expect(tab.Locator(".feedback__verdict--knew")).ToBeVisibleAsync();
        await Assertions.Expect(tab.Locator(".self__grades")).ToHaveCountAsync(0);
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Practice_OutboxWriteFails_ShowsStorageStateAndContinues()
    {
        await using IBrowserContext context = await NewContextAsync();
        await context.AddInitScriptAsync(OutboxGate(failAppends: true));
        IPage tab = await host.NewPageAsync(context);
        await OpenFirstAsync(tab, SingleId);

        await AnswerShownAsync(tab);

        await Assertions.Expect(tab.Locator(".storage-notice")).ToHaveTextAsync(Resource("en", "Storage.Full.One").Replace("{0}", "1", StringComparison.Ordinal));
        Assert.Null(await StoredAsync(tab, AnonymousOutbox));
        await NextAsync(tab);
        await AnswerShownAsync(tab);
        await Assertions.Expect(tab.Locator(".storage-notice")).ToContainTextAsync("2");
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Practice_StartDoubleClick_FetchesOnce()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await host.NewPageAsync(context);
        await tab.GotoAsync(PracticePath);
        await WaitForIslandAsync(tab);

        await tab.Locator(".pf__start").DblClickAsync();

        await tab.Locator(".qcard").WaitForAsync();
        await tab.WaitForLoadStateAsync(LoadState.NetworkIdle);
        Assert.Equal(1, host.SessionRequestCount(tab));
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Practice_WithStorageBlocked_ShowsNotice()
    {
        await using IBrowserContext context = await NewContextAsync();
        // No Web Locks: the outbox cannot be written safely, so answers stay in this tab and the page says so.
        await context.AddInitScriptAsync("delete Navigator.prototype.locks;");
        IPage tab = await host.NewPageAsync(context);
        await OpenFirstAsync(tab, SingleId);

        await AnswerShownAsync(tab);

        await Assertions.Expect(tab.Locator(".storage-notice")).ToHaveTextAsync(Resource("en", "Storage.MemoryOnly"));
        await Assertions.Expect(tab.Locator(".storage-notice")).ToBeVisibleAsync();
        Assert.Null(await StoredAsync(tab, AnonymousOutbox));
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Practice_AnswersOnlyInMemory_WarnBeforeLeaving()
    {
        await using IBrowserContext context = await NewContextAsync();
        await context.AddInitScriptAsync("delete Navigator.prototype.locks;");
        IPage tab = await host.NewPageAsync(context);
        await OpenFirstAsync(tab, SingleId);
        await AnswerShownAsync(tab);
        await Assertions.Expect(tab.Locator(".storage-notice")).ToBeVisibleAsync();

        List<string> dialogs = await LeaveAsync(tab);

        // Closing the tab or the sign-out POST would drop answers that exist only in this tab.
        Assert.Equal(["beforeunload"], dialogs);
    }

    [Fact]
    public async Task Practice_AnswersStored_LeaveWithoutWarning()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await host.NewPageAsync(context);
        await OpenFirstAsync(tab, SingleId);
        await AnswerShownAsync(tab);
        await tab.WaitForFunctionAsync("k => localStorage.getItem(k) !== null", AnonymousOutbox);

        List<string> dialogs = await LeaveAsync(tab);

        Assert.Empty(dialogs);
    }

    // Closes the tab with its beforeunload handlers and returns the dialogs it raised (each accepted, so it closes).
    private static async Task<List<string>> LeaveAsync(IPage tab)
    {
        List<string> dialogs = [];
        tab.Dialog += async (_, dialog) =>
        {
            lock (dialogs)
            {
                dialogs.Add(dialog.Type);
            }

            await dialog.AcceptAsync();
        };
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        tab.Close += (_, _) => closed.TrySetResult();
        await tab.CloseAsync(new PageCloseOptions { RunBeforeUnload = true });
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        lock (dialogs)
        {
            return [.. dialogs];
        }
    }

    [Fact]
    public async Task Practice_FirstQuestion_StartsImmediately()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await host.NewPageAsync(context);

        JsonElement session = await OpenFirstAsync(tab, ClosureId, "&level=senior");

        Assert.Equal(ClosureId, session.GetProperty("questions")[0].GetProperty("id").GetString());
        await Assertions.Expect(tab.Locator(".pf")).ToHaveCountAsync(0);
        await Assertions.Expect(tab.Locator(".qcard__title")).ToHaveTextAsync("What does the code print when lambdas capture a for loop variable?");
        Assert.Equal(1, host.SessionRequestCount(tab));
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Practice_RemembersLastFilters()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await host.NewPageAsync(context);
        await tab.GotoAsync(PracticePath);
        await WaitForIslandAsync(tab);
        await tab.Locator(".pf input[name='level'][value='middle']").CheckAsync();
        await tab.Locator(".pf input[name='type'][value='single']").CheckAsync();
        await tab.Locator(".pf input[name='status'][value='new']").CheckAsync();
        await tab.Locator(".pf input[name='count']").FillAsync("3");
        SessionTap tap = await TapAsync(tab);
        await PressAsync(tab, tap, ".pf__start");
        JsonElement request = JsonDocument.Parse(tap.RequestBody(tap.Count - 1)).RootElement;
        Assert.Equal(["middle"], request.GetProperty("levels").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("new", request.GetProperty("status").GetString());
        Assert.Equal(3, request.GetProperty("count").GetInt32());

        await tab.GotoAsync(PracticePath);
        await WaitForIslandAsync(tab);
        await Assertions.Expect(tab.Locator(".pf input[name='level'][value='middle']")).ToBeCheckedAsync();
        await Assertions.Expect(tab.Locator(".pf input[name='type'][value='single']")).ToBeCheckedAsync();
        await Assertions.Expect(tab.Locator(".pf input[name='status'][value='new']")).ToBeCheckedAsync();
        await Assertions.Expect(tab.Locator(".pf input[name='count']")).ToHaveValueAsync("3");

        // Filters in the URL win over the remembered ones; the remembered count stays.
        await tab.GotoAsync(PracticePath + "?topic=async");
        await WaitForIslandAsync(tab);
        await Assertions.Expect(tab.Locator(".pf input[name='topic'][value='async']")).ToBeCheckedAsync();
        await Assertions.Expect(tab.Locator(".pf input[name='level'][value='middle']")).Not.ToBeCheckedAsync();
        await Assertions.Expect(tab.Locator(".pf input[name='status'][value='all']")).ToBeCheckedAsync();
        await Assertions.Expect(tab.Locator(".pf input[name='count']")).ToHaveValueAsync("3");
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Practice_NewSession_ExcludesJustSeen()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await host.NewPageAsync(context);
        JsonElement first = await StartAsync(tab, count: 2);
        string[] seen = [.. first.GetProperty("questions").EnumerateArray().Select(q => q.GetProperty("id").GetString()!)];
        Assert.Equal(2, seen.Length);

        await AnswerShownAsync(tab);
        await NextAsync(tab);
        await AnswerShownAsync(tab);
        await NextAsync(tab);
        await Assertions.Expect(tab.Locator(".practice-summary")).ToBeVisibleAsync();
        await Assertions.Expect(tab.Locator(".practice-summary a[href^='en/dotnet/questions/']")).ToHaveCountAsync(2);

        SessionTap tap = await TapAsync(tab);
        JsonElement next = await PressAsync(tab, tap, ".practice-summary__new");

        string[] excluded = [.. JsonDocument.Parse(tap.RequestBody(tap.Count - 1)).RootElement.GetProperty("excludeQuestionIds").EnumerateArray().Select(e => e.GetString()!)];
        Assert.Equal(seen.Order(StringComparer.Ordinal), excluded.Order(StringComparer.Ordinal));
        Assert.All(next.GetProperty("questions").EnumerateArray(), q => Assert.DoesNotContain(q.GetProperty("id").GetString(), seen));
        await tab.Locator(".qcard").WaitForAsync();
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Practice_NewSession_KeepsFocusOffTheBodyWhileLoading()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await host.NewPageAsync(context);
        await StartAsync(tab, count: 1);
        await AnswerShownAsync(tab);
        await NextAsync(tab);
        await Assertions.Expect(tab.Locator(".practice-summary")).ToBeVisibleAsync();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Registered last, so it runs first and holds the request before the tap sees it.
        await tab.RouteAsync("**/api/sessions/practice", async route =>
        {
            await release.Task;
            await route.FallbackAsync();
        });

        await tab.Locator(".practice-summary__new").FocusAsync();
        await tab.Keyboard.PressAsync("Enter");
        await tab.Locator(".island-state").WaitForAsync();

        // Moving focus is one interop call after the render, so give it a moment; the request is still held.
        bool onBody = false;
        try
        {
            await tab.WaitForFunctionAsync(
                "() => document.activeElement !== null && document.activeElement !== document.body",
                null,
                new PageWaitForFunctionOptions { Timeout = 3000 });
        }
        catch (TimeoutException)
        {
            onBody = true;
        }

        release.SetResult();
        Assert.False(onBody, "Focus fell to the body while the new session was loading.");
        await tab.Locator(".qcard").WaitForAsync();
        host.AssertNoCspViolations(tab);
    }

    [Theory]
    [InlineData(375)]
    [InlineData(768)]
    [InlineData(1280)]
    public async Task Practice_SidebarDrawerOnPhone_CollapsibleOnTablet(int width)
    {
        await using IBrowserContext context = await NewContextAsync(width: width);
        IPage tab = await host.NewPageAsync(context);
        await OpenFirstAsync(tab, SingleId);
        ILocator side = tab.Locator("details.side");
        ILocator item = side.Locator(".side__item").First;

        if (width < 1024)
        {
            Assert.False(await side.EvaluateAsync<bool>("e => e.open"));
            Assert.False(await item.IsVisibleAsync());
            await side.Locator("summary").ClickAsync();
            Assert.True(await item.IsVisibleAsync());
            await side.Locator("summary").ClickAsync();
            Assert.False(await item.IsVisibleAsync());
        }
        else
        {
            Assert.False(await side.Locator("summary").IsVisibleAsync());
            Assert.True(await item.IsVisibleAsync());
        }

        host.AssertNoCspViolations(tab);
    }

    // Spec 9.1 with the mockup: one column on phones, two on tablets, and from 1024 px short options (the closure
    // outputs) in one row while sentence-long options (the single-choice question) keep two columns.
    [Theory]
    [InlineData(375, ClosureId, 1)]
    [InlineData(768, ClosureId, 2)]
    [InlineData(1280, ClosureId, 3)]
    [InlineData(1280, SingleId, 2)]
    public async Task Practice_OptionColumns_FollowTheLayout(int width, string questionId, int columns)
    {
        await using IBrowserContext context = await NewContextAsync(width: width);
        IPage tab = await host.NewPageAsync(context);
        JsonElement session = await OpenFirstAsync(tab, questionId);
        int options = Question(session, questionId).GetProperty("options").GetArrayLength();
        ILocator grid = tab.Locator(".opts--pick");

        int rendered = await grid.EvaluateAsync<int>("g => getComputedStyle(g).gridTemplateColumns.split(' ').length");
        // Every option of a one-row layout really sits on one row.
        int rows = await grid.EvaluateAsync<int>("g => new Set([...g.children].map(c => Math.round(c.getBoundingClientRect().top))).size");

        Assert.Equal(columns, rendered);
        Assert.Equal((options + columns - 1) / columns, rows);
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
    public async Task Practice_NoHorizontalOverflow(int width, string theme)
    {
        await using IBrowserContext context = await NewContextAsync(width: width, theme: theme);
        IPage tab = await host.NewPageAsync(context);
        JsonElement session = await OpenFirstAsync(tab, SingleId);
        string wrong = Question(session, SingleId).GetProperty("options").EnumerateArray()
            .First(o => !o.GetProperty("correct").GetBoolean()).GetProperty("id").GetString()!;
        await tab.Locator($".opt--pick:has(input[value='{wrong}'])").ClickAsync();
        await SubmitAsync(tab);
        await tab.EvaluateAsync("document.fonts.ready");

        int scrollWidth = await tab.EvaluateAsync<int>("document.documentElement.scrollWidth");
        int innerWidth = await tab.EvaluateAsync<int>("window.innerWidth");
        Directory.CreateDirectory(ScreenshotDirectory);
        await tab.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(ScreenshotDirectory, $"practice-{width}-{theme}.png"), FullPage = true });

        Assert.True(scrollWidth <= innerWidth, $"practice at {width} px ({theme}): scrollWidth {scrollWidth} > innerWidth {innerWidth}.");
        Assert.True(await tab.Locator(".practice__next").IsVisibleAsync());
        host.AssertNoCspViolations(tab);
    }

    [Theory]
    [InlineData(375, true, true)]
    [InlineData(1280, false, false)]
    [InlineData(1280, true, false)]
    public async Task Practice_TouchTargetsAtLeast44px(int width, bool touch, bool mobile)
    {
        await using IBrowserContext context = await NewContextAsync(width: width, touch: touch, mobile: mobile);
        IPage tab = await host.NewPageAsync(context);
        await tab.GotoAsync(PracticePath);
        await WaitForIslandAsync(tab);
        await AssertTargetsAsync(tab, "filters");

        await OpenFirstAsync(tab, SingleId);
        if (width < 1024)
        {
            await tab.Locator("details.side summary").ClickAsync();
        }

        await AssertTargetsAsync(tab, "question");
        await tab.Locator(".opt--pick").First.ClickAsync();
        await SubmitAsync(tab);
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

    [Fact]
    public async Task Practice_StickyActionVisibleOnPhone()
    {
        await using IBrowserContext context = await NewContextAsync(width: 375, height: 667, touch: true, mobile: true);
        IPage tab = await host.NewPageAsync(context);
        await OpenFirstAsync(tab, FindBugId);
        ILocator answer = tab.Locator(".practice__answer");

        Assert.Equal("sticky", await tab.Locator(".action-bar").EvaluateAsync<string>("e => getComputedStyle(e).position"));
        await AssertInViewportAsync(tab, answer, "Answer before scrolling");
        await tab.Locator(".code-line").Last.ScrollIntoViewIfNeededAsync();
        await AssertInViewportAsync(tab, answer, "Answer after scrolling");

        await tab.Locator(".code-line").Nth(1).TapAsync();
        await answer.TapAsync();
        await tab.Locator(".feedback").WaitForAsync();
        await AssertInViewportAsync(tab, tab.Locator(".practice__next"), "Next");
        host.AssertNoCspViolations(tab);
    }

    private static async Task AssertInViewportAsync(IPage tab, ILocator element, string what)
    {
        LocatorBoundingBoxResult box = (await element.BoundingBoxAsync())!;
        int height = await tab.EvaluateAsync<int>("window.innerHeight");
        Assert.True(box.Y >= 0 && box.Y + box.Height <= height + 0.5, $"{what} is outside the viewport: y {box.Y}, height {box.Height}, viewport {height}.");
    }

    [Fact]
    public async Task Practice_TypedInputVisibleWhenViewportShrinks()
    {
        await using IBrowserContext context = await NewContextAsync(width: 375, height: 667, touch: true, mobile: true);
        IPage tab = await host.NewPageAsync(context);
        await RouteClosureAsTypedAsync(tab);
        await OpenFirstAsync(tab, ClosureId);
        ILocator input = tab.Locator(".typed__input");

        await input.FocusAsync();
        // What an on-screen keyboard does to a page with interactive-widget=resizes-content.
        await tab.SetViewportSizeAsync(375, 360);
        await tab.WaitForFunctionAsync("() => window.innerHeight === 360");
        await tab.WaitForTimeoutAsync(300);

        await AssertInViewportAsync(tab, input, "The typed input");
        LocatorBoundingBoxResult field = (await input.BoundingBoxAsync())!;
        LocatorBoundingBoxResult bar = (await tab.Locator(".action-bar").BoundingBoxAsync())!;
        Assert.True(field.Y + field.Height <= bar.Y + 0.5, $"The sticky bar covers the input: input bottom {field.Y + field.Height}, bar top {bar.Y}.");
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Practice_RuCulture_UsesRuStrings()
    {
        await using IBrowserContext context = await NewContextAsync(locale: "ru-RU");
        IPage tab = await host.NewPageAsync(context);
        await tab.GotoAsync("/ru/dotnet/practice");
        await WaitForIslandAsync(tab);
        await Assertions.Expect(tab.Locator(".pf__start")).ToHaveTextAsync(Resource("ru", "Practice.Start"));

        await StartAsync(tab, "/ru/dotnet/practice?level=architect");
        await Assertions.Expect(tab.Locator(".island-state p")).ToHaveTextAsync(Resource("ru", "State.Empty"));

        // A URL selection, so the remembered architect filter from above does not apply and there is a next question.
        await tab.GotoAsync($"/ru/dotnet/practice?first={SingleId}&level=middle");
        await AnswerShownAsync(tab);
        await Assertions.Expect(tab.Locator(".feedback")).ToHaveAttributeAsync("aria-label", Resource("ru", "Feedback.Label"));
        await Assertions.Expect(tab.Locator(".practice__next")).ToContainTextAsync(Resource("ru", "Practice.Next"));
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task IslandToPublicToIsland_OneSessionFetchPerVisit()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await host.NewPageAsync(context);

        await StartAsync(tab);
        await tab.Locator(".qcard").WaitForAsync();
        await tab.WaitForLoadStateAsync(LoadState.NetworkIdle);
        Assert.Equal(1, host.SessionRequestCount(tab));
        Assert.Equal(1, await tab.EvaluateAsync<int>("() => window.__ditStarts || 0"));

        await tab.Locator(".qcard__title").WaitForAsync();
        await tab.GotoAsync("/en/dotnet/questions/closure-for-loop");
        await tab.WaitForLoadStateAsync(LoadState.NetworkIdle);
        Assert.Equal(1, host.SessionRequestCount(tab));

        await tab.Locator($"a[href='/en/dotnet/practice?first={ClosureId}']").ClickAsync();
        await tab.WaitForURLAsync($"**/en/dotnet/practice?first={ClosureId}");
        await tab.Locator(".qcard").WaitForAsync(new LocatorWaitForOptions { Timeout = (float)IslandTimeout.TotalMilliseconds });
        await tab.WaitForLoadStateAsync(LoadState.NetworkIdle);

        Assert.Equal(2, host.SessionRequestCount(tab));
        Assert.Equal(1, await tab.EvaluateAsync<int>("() => window.__ditStarts || 0"));
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Preload_UrlsMatchRuntimeRequests_DebugHost()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await host.NewPageAsync(context);
        await tab.GotoAsync("/en");
        await tab.WaitForLoadStateAsync(LoadState.NetworkIdle);
        Assert.Equal(0, await tab.Locator("link[rel='prefetch']").CountAsync());

        ILocator card = tab.Locator("a.mode-card[href='/en/dotnet/practice']");
        await card.HoverAsync();
        await tab.Locator("a.mode-card[href='/en/dotnet/mock']").HoverAsync();
        await card.FocusAsync();
        await Assertions.Expect(tab.Locator("link[rel='prefetch']")).ToHaveCountAsync(2);
        string[] prefetched = await tab.EvaluateAsync<string[]>("() => [...document.querySelectorAll('link[rel=prefetch]')].map(l => new URL(l.href).pathname)");

        List<string> requests = [];
        tab.Request += (_, request) => requests.Add(new Uri(request.Url).AbsolutePath);
        await card.ClickAsync();
        await WaitForIslandAsync(tab);
        await tab.WaitForLoadStateAsync(LoadState.NetworkIdle);

        Assert.All(prefetched, url => Assert.Contains(url, requests));
        host.AssertNoCspViolations(tab);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoTabs_AnswersOfAStayWithA_WhenBSignsIn(bool suppressBroadcast)
    {
        long a = suppressBroadcast ? 990_300_011L : 990_300_021L;
        long b = suppressBroadcast ? 990_300_012L : 990_300_022L;
        string aKey = $"dit.outbox.v1.u.{a}";
        await using IBrowserContext context = await NewContextAsync();
        await host.SignInAsync(context, new FakeTelegramIdentity(a, "Account A", "account_a"));
        IPage tab1 = await host.NewPageAsync(context);
        if (suppressBroadcast)
        {
            // Storage events about dit.account never reach the island, so only the server's owner check can stop it.
            await tab1.AddInitScriptAsync(
                """
                (() => {
                  const add = window.addEventListener;
                  window.addEventListener = function (type, listener, options) {
                    if (type === 'storage') {
                      const inner = listener;
                      listener = function (e) { if (e.key !== 'dit.account') { return inner.call(this, e); } };
                    }
                    return add.call(this, type, listener, options);
                  };
                })();
                """);
        }

        List<int> answerStatuses = [];
        tab1.Response += (_, response) =>
        {
            if (new Uri(response.Url).AbsolutePath == "/api/answers/batch")
            {
                lock (answerStatuses)
                {
                    answerStatuses.Add(response.Status);
                }
            }
        };
        await OpenFirstAsync(tab1, SingleId);
        await WaitForIslandAsync(tab1);

        // This tab alone goes offline: the other tab still has to sign in.
        await tab1.RouteAsync("**/api/**", route => route.AbortAsync());
        await AnswerShownAsync(tab1);
        await NextAsync(tab1);
        await AnswerShownAsync(tab1);
        Assert.Equal(2, await StoredCountAsync(tab1, aKey));

        IPage tab2 = await host.NewPageAsync(context);
        await tab2.GotoAsync("/en/about");
        await tab2.Locator(".account__logout button").ClickAsync();
        await tab2.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await host.SignInAsync(tab2, new FakeTelegramIdentity(b, "Account B", "account_b"));
        // B's signed-in page published B, not just the sign-out's anon, so tab 1 reacts to B.
        await tab2.WaitForFunctionAsync($"() => (localStorage.getItem('dit.account') || '').split('|')[0] === '{b}'");

        if (!suppressBroadcast)
        {
            await Assertions.Expect(tab1.Locator(".account-changed")).ToBeVisibleAsync();
            await tab1.UnrouteAllAsync();
            // Longer than the first two back-off delays: a sync that had not stopped would have sent by now.
            await tab1.WaitForTimeoutAsync(16_000);
            lock (answerStatuses)
            {
                Assert.Empty(answerStatuses);
            }
        }
        else
        {
            Assert.False(await tab1.Locator(".account-changed").IsVisibleAsync());
            Task<IResponse> refused = tab1.WaitForResponseAsync(
                r => new Uri(r.Url).AbsolutePath == "/api/answers/batch",
                new PageWaitForResponseOptions { Timeout = 60_000 });
            await tab1.UnrouteAllAsync();
            Assert.Equal(409, (await refused).Status);
            await Assertions.Expect(tab1.Locator(".account-changed")).ToBeVisibleAsync();
        }

        Assert.Equal(2, await StoredCountAsync(tab1, aKey));
        Assert.Equal(0, await ServerAnswersAsync(b));
        Assert.Equal(0, await ServerAnswersAsync(a));

        await host.SignInAsync(context, new FakeTelegramIdentity(a, "Account A", "account_a"));
        await tab1.ReloadAsync();
        await WaitForIslandAsync(tab1);
        await WaitForServerAnswersAsync(a, 2, TimeSpan.FromSeconds(30));
        await tab1.WaitForFunctionAsync($"() => {{ const v = localStorage.getItem('{aKey}'); return v === null || JSON.parse(v).answers.length === 0; }}");
        Assert.Equal(0, await ServerAnswersAsync(b));
        host.AssertNoCspViolations(tab1);
        host.AssertNoCspViolations(tab2);
    }
}
