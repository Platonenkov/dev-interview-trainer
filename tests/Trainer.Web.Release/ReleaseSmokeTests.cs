using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Trainer.Contracts;
using Trainer.Domain.Ids;

namespace Trainer.Web.Release;

/// <summary>
/// The published Release build in a real browser: the islands boot from trimmed, fingerprinted, compressed assets, talk
/// to the API with the real DTOs, load each runtime file once, keep it cached, and public pages never load it.
/// </summary>
[Trait("Category", "Release")]
public sealed partial class ReleaseSmokeTests(PublishedAppFixture app)
{
    private const string ReadyIsland = ".island-root[data-island-ready='true']";
    private const string SingleQuestionId = "01M76SZRCB0WKNBTT7M8RG2KHR";
    private const string AnswerRowsSql = "SELECT count(*) FROM answers a JOIN users u ON u.id = a.user_id WHERE u.telegram_user_id = @tg";
    private const string MockSessionRowsSql = "SELECT count(*) FROM mock_sessions m JOIN users u ON u.id = m.user_id WHERE u.telegram_user_id = @tg";
    private const string MockAnswerRowsSql = AnswerRowsSql + " AND a.mock_session_id IS NOT NULL";
    private const string ReviewAnswerRowsSql = AnswerRowsSql + " AND a.mode = 3";

    private static readonly string[] AllQuestionTypes = ["open", "single", "multi", "output", "find-bug", "situation"];
    private static readonly float Wait = 60_000;

    [GeneratedRegex("<script type=\"importmap\" nonce=\"(?<nonce>[A-Za-z0-9_-]{24})\">(?<map>[^<]*)</script>")]
    private static partial Regex ImportMapScript();

    [GeneratedRegex("(?:src|href)=\"(?<url>/[^\"]+\\.(?:css|js))\"")]
    private static partial Regex AssetReference();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task Practice_PublishedBuild_RoundTripsRealDtos(string culture)
    {
        app.SkipUnlessAvailable();
        await using IBrowserContext context = await app.NewContextAsync(locale: culture == "ru" ? "ru-RU" : "en-US");
        long telegramId = await app.SignInAsync(context, "Release Practice");
        IPage tab = await app.NewPageAsync(context);
        NetworkRecorder network = await NetworkRecorder.AttachAsync(context, tab);
        ApiTap sessions = await ApiTap.AttachAsync(tab, "**/api/sessions/practice");
        ApiTap batches = await ApiTap.AttachAsync(tab, "**/api/answers/batch");

        await tab.GotoAsync($"/{culture}/dotnet/practice");
        await tab.Locator(ReadyIsland).WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        Assert.Equal(culture, await tab.Locator(".island-root").GetAttributeAsync("data-island-culture"));
        Assert.Equal("true", await tab.Locator(".island-root").GetAttributeAsync("data-island-signed-in"));
        await tab.Locator(".pf input[name='count']").FillAsync("50");
        await tab.Locator(".pf__start").ClickAsync();

        ApiExchange exchange = await sessions.WaitForAsync(1);
        PracticeSessionRequest request = JsonSerializer.Deserialize(exchange.RequestBody!, TrainerJsonContext.Default.PracticeSessionRequest)!;
        SessionDto session = JsonSerializer.Deserialize(exchange.ResponseBody, TrainerJsonContext.Default.SessionDto)!;
        Assert.Equal(culture, request.Culture);
        Assert.Equal("dotnet", request.Stack);
        // The session is a random draw of at most 50 questions from the real content; once a topic outgrows that,
        // a draw can miss a type, so only membership is checked.
        Assert.NotEmpty(session.Questions);
        Assert.Subset(AllQuestionTypes.ToHashSet(StringComparer.Ordinal), session.Questions.Select(q => q.Type).ToHashSet(StringComparer.Ordinal));

        for (int i = 0; i < session.Questions.Count; i++)
        {
            await AnswerShownAsync(tab);
            await tab.Locator(".practice__next").ClickAsync();
        }

        await tab.Locator(".practice-summary").WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        await app.WaitForCountAsync(AnswerRowsSql, telegramId, session.Questions.Count);
        await tab.WaitForFunctionAsync(
            $"() => {{ const v = localStorage.getItem('dit.outbox.v1.u.{telegramId}'); return v === null || JSON.parse(v).answers.length === 0; }}",
            null,
            new PageWaitForFunctionOptions { Timeout = Wait });

        List<AnswerDto> sent = [];
        HashSet<string> accepted = new(StringComparer.Ordinal);
        foreach (ApiExchange batch in batches.Exchanges)
        {
            Assert.Equal(HttpStatusCode.OK, batch.Status);
            AnswerBatch body = JsonSerializer.Deserialize(batch.RequestBody!, TrainerJsonContext.Default.AnswerBatch)!;
            Assert.Equal(AnswerBatch.CurrentVersion, body.Version);
            sent.AddRange(body.Answers);
            AnswerBatchResponse response = JsonSerializer.Deserialize(batch.ResponseBody, TrainerJsonContext.Default.AnswerBatchResponse)!;
            Assert.Empty(response.Rejected);
            accepted.UnionWith(response.Accepted);
        }

        IReadOnlyList<(string ClientAnswerId, string QuestionId, short Mode)> stored = await app.StoredAnswersAsync(telegramId);
        Assert.Equal(sent.Select(a => a.ClientAnswerId).Order(StringComparer.Ordinal), accepted.Order(StringComparer.Ordinal));
        Assert.Equal(accepted.Order(StringComparer.Ordinal), stored.Select(s => s.ClientAnswerId).Order(StringComparer.Ordinal));
        Assert.Equal(session.Questions.Select(q => q.Id).Order(StringComparer.Ordinal), stored.Select(s => s.QuestionId).Order(StringComparer.Ordinal));
        Assert.All(sent, a => Assert.Equal("practice", a.Mode));
        Assert.All(stored, s => Assert.Equal(1, s.Mode));

        Assert.Equal(1, sessions.Count);
        Assert.Equal(1, await tab.EvaluateAsync<int>("() => window.__ditStarts || 0"));
        AssertEveryFrameworkFileOnce(network);
        app.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task MockAndReview_PublishedBuild_Work()
    {
        app.SkipUnlessAvailable();
        await using IBrowserContext context = await app.NewContextAsync();
        long telegramId = await app.SignInAsync(context, "Release Mock");
        IPage tab = await app.NewPageAsync(context);
        ApiTap mocks = await ApiTap.AttachAsync(tab, "**/api/sessions/mock");
        string progress = $"dit.mock.v1.{telegramId.ToString(CultureInfo.InvariantCulture)}.active";

        await tab.GotoAsync("/en/dotnet/mock");
        await tab.Locator(ReadyIsland).WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        await tab.Locator(".mock-setup input[name='mock-level'][value='senior']").CheckAsync();
        await tab.Locator(".mock-setup__start").ClickAsync();
        await PhaseAsync(tab, "prepared", "answering");
        if (await tab.Locator(".mock[data-mock-phase='prepared']").CountAsync() > 0)
        {
            await tab.Locator(".mock-warn__anyway").ClickAsync();
        }

        await PhaseAsync(tab, "answering");
        MockSessionDto mock = JsonSerializer.Deserialize((await mocks.WaitForAsync(1)).ResponseBody, TrainerJsonContext.Default.MockSessionDto)!;
        Assert.NotEmpty(mock.Questions);
        int questions = await tab.Locator(".jump__btn").CountAsync();
        for (int i = 0; i < questions; i++)
        {
            await tab.Locator(".jump__btn").Nth(i).ClickAsync();
            await GiveMockResponseAsync(tab, progress);
        }

        await tab.Locator(".mock__finish").ClickAsync();
        await PhaseAsync(tab, "assessment", "summary");
        while (await tab.Locator(".mock[data-mock-phase='assessment']").CountAsync() > 0)
        {
            await tab.Locator(".mock-assess__item:has(.self__grades)").First.Locator(".self__grade--didnt-know").ClickAsync();
            await PhaseAsync(tab, "assessment", "summary");
            await tab.WaitForFunctionAsync("() => !document.querySelector('.mock-assess [role=status]')", null, new PageWaitForFunctionOptions { Timeout = Wait });
        }

        await PhaseAsync(tab, "summary");
        int graded = await tab.Locator(".mock-summary__list .practice-summary__mark:not(.practice-summary__mark--none)").CountAsync();
        Assert.Equal(questions, graded);
        await app.WaitForCountAsync(MockSessionRowsSql, telegramId, 1);
        await app.WaitForCountAsync(MockAnswerRowsSql, telegramId, graded);
        Assert.Equal(1, mocks.Count);
        Assert.Equal(1, await tab.EvaluateAsync<int>("() => window.__ditStarts || 0"));
        app.AssertNoCspViolations(tab);

        await app.MakeReviewsDueAsync(telegramId);
        ApiTap reviews = await ApiTap.AttachAsync(tab, "**/api/sessions/review");
        await tab.GotoAsync("/en/dotnet/review");
        SessionDto due = JsonSerializer.Deserialize((await reviews.WaitForAsync(1)).ResponseBody, TrainerJsonContext.Default.SessionDto)!;
        Assert.NotEmpty(due.Questions);
        await tab.Locator(".qcard").WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        if (await tab.Locator(".recall__show").CountAsync() > 0)
        {
            await tab.Locator(".recall__show").ClickAsync();
        }

        await AnswerShownAsync(tab, submit: ".review__answer");
        await app.WaitForCountAsync(ReviewAnswerRowsSql, telegramId, 1);
        Assert.Equal(1, reviews.Count);
        Assert.Equal(1, await tab.EvaluateAsync<int>("() => window.__ditStarts || 0"));
        app.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Profile_PublishedBuild_Boots()
    {
        app.SkipUnlessAvailable();
        await using IBrowserContext context = await app.NewContextAsync();
        await app.SignInAsync(context, "Release Profile");
        IPage tab = await app.NewPageAsync(context);
        NetworkRecorder network = await NetworkRecorder.AttachAsync(context, tab);
        ApiTap profiles = await ApiTap.AttachAsync(tab, "**/api/profile*");

        await tab.GotoAsync("/en/dotnet/profile");
        await tab.Locator(ReadyIsland).WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        await network.WaitForFrameworkSettledAsync();

        ApiExchange exchange = await profiles.WaitForAsync(1);
        Assert.Equal(HttpStatusCode.OK, exchange.Status);
        Assert.NotNull(JsonSerializer.Deserialize(exchange.ResponseBody, TrainerJsonContext.Default.ProfileDto));
        Assert.Equal("true", await tab.Locator(".island-root").GetAttributeAsync("data-island-signed-in"));
        Assert.Equal(1, await tab.EvaluateAsync<int>("() => window.__ditStarts || 0"));
        AssertEveryFrameworkFileOnce(network);
        app.AssertNoCspViolations(tab);
    }

    // The client has no culture data (ADR 0013): dates come from the browser's Intl in the device's zone, plural forms
    // from the culture name. A mock finished at 21:30 UTC on 7 March is 8 March in Novosibirsk (UTC+7).
    [Theory]
    [InlineData("ru", "08.03.2026", "1 день", "3 вопроса", @"^\d{2}\.\d{2}\.\d{4}, \d{2}:\d{2}$")]
    [InlineData("en", "3/8/2026", "1 day", "3 questions", @"^\d{1,2}/\d{1,2}/\d{4}, \d{1,2}:\d{2}\s(AM|PM)$")]
    public async Task Profile_FormatsDatesAndCountsInThePageLanguage(string culture, string mockDate, string streak, string questions, string nextPattern)
    {
        app.SkipUnlessAvailable();
        await using IBrowserContext context = await app.NewContextAsync(locale: culture == "ru" ? "ru-RU" : "en-US", timezoneId: "Asia/Novosibirsk");
        long telegramId = await app.SignInAsync(context, "Release Formats");
        IPage tab = await app.NewPageAsync(context);
        await tab.GotoAsync($"/{culture}/dotnet/practice?first={SingleQuestionId}");
        await AnswerShownAsync(tab);
        await app.WaitForCountAsync(AnswerRowsSql, telegramId, 1);
        await app.AddMockSessionAsync(telegramId, new UlidGenerator(TimeProvider.System).Next(), 3, new DateTimeOffset(2026, 3, 7, 21, 30, 0, TimeSpan.Zero));

        await tab.GotoAsync($"/{culture}/dotnet/profile");
        await tab.Locator(".profile__stats").WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });

        Assert.Equal(streak, (await tab.Locator(".profile__streak").InnerTextAsync()).Trim());
        Assert.Equal(mockDate, (await tab.Locator(".profile__mocks time").InnerTextAsync()).Trim());
        Assert.Equal(questions, (await tab.Locator(".profile__mocks li span:not(.level-chip):not(.profile__counts)").First.InnerTextAsync()).Trim());
        ILocator next = tab.Locator(".profile__next time");
        string nextText = (await next.InnerTextAsync()).Trim();
        Assert.Matches(nextPattern, nextText);
        DateTimeOffset nextAt = DateTimeOffset.Parse((await next.GetAttributeAsync("datetime"))!, CultureInfo.InvariantCulture);
        DateTimeOffset local = nextAt.ToOffset(TimeSpan.FromHours(7));
        string expectedDate = culture == "ru"
            ? local.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
            : local.ToString("M/d/yyyy", CultureInfo.InvariantCulture);
        Assert.StartsWith(expectedDate + ", ", nextText, StringComparison.Ordinal);
        Assert.Equal(culture, await tab.Locator(".island-root").GetAttributeAsync("data-island-culture"));
        app.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task PublicPages_RequestNoFrameworkFiles()
    {
        app.SkipUnlessAvailable();
        await using IBrowserContext context = await app.NewContextAsync();
        IPage tab = await app.NewPageAsync(context);
        NetworkRecorder network = await NetworkRecorder.AttachAsync(context, tab);

        foreach (string path in new[] { "/en", "/en/dotnet/questions", "/en/dotnet/questions/closure-for-loop", "/ru/dotnet/questions" })
        {
            IResponse response = (await tab.GotoAsync(path))!;
            await tab.WaitForLoadStateAsync(LoadState.NetworkIdle);
            string html = await response.TextAsync();
            Assert.DoesNotContain("<script src=\"/_framework/", html, StringComparison.Ordinal);
            Assert.DoesNotContain("importmap", html, StringComparison.Ordinal);
            Assert.DoesNotContain("nonce-", response.Headers["content-security-policy"], StringComparison.Ordinal);
        }

        Assert.Empty(network.Framework.Select(r => r.Path));
        app.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Preload_UrlsMatchRuntimeRequests()
    {
        app.SkipUnlessAvailable();
        await using IBrowserContext context = await app.NewContextAsync();
        IPage tab = await app.NewPageAsync(context);
        NetworkRecorder network = await NetworkRecorder.AttachAsync(context, tab);
        await tab.GotoAsync("/en");
        await tab.WaitForLoadStateAsync(LoadState.NetworkIdle);

        ILocator card = tab.Locator("a.mode-card[href='/en/dotnet/practice']");
        await card.HoverAsync();
        await Assertions.Expect(tab.Locator("link[rel='prefetch']")).ToHaveCountAsync(2);
        string[] prefetched = await tab.EvaluateAsync<string[]>("() => [...document.querySelectorAll('link[rel=prefetch]')].map(l => new URL(l.href).pathname)");
        await network.WaitForFrameworkSettledAsync();
        Assert.All(prefetched, url => Assert.True(app.Assets.IsFingerprinted(url), $"{url} is not a fingerprinted asset."));
        Assert.Equal(prefetched.Order(StringComparer.Ordinal), network.Framework.Select(r => r.Path).Order(StringComparer.Ordinal));

        network.Clear();
        await card.ClickAsync();
        await tab.Locator(ReadyIsland).WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        await network.WaitForFrameworkSettledAsync();

        RecordedRequest[] runtime = [.. network.Framework];
        foreach (string url in prefetched)
        {
            RecordedRequest[] uses = [.. runtime.Where(r => r.Path == url)];
            Assert.True(uses.Length == 1, $"The island page requested the prefetched {url} {uses.Length} times; it requested: {Describe(runtime)}");
            Assert.True(uses[0].FromCache, $"The prefetched {url} was downloaded again.");
        }

        AssertEveryFrameworkFileOnce(network);
        app.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task ColdLoad_RequestsEveryFrameworkFileOnce_UnderItsFingerprintedName()
    {
        app.SkipUnlessAvailable();
        await using IBrowserContext context = await app.NewContextAsync();
        IPage tab = await app.NewPageAsync(context);
        NetworkRecorder network = await NetworkRecorder.AttachAsync(context, tab);

        await tab.GotoAsync("/en/dotnet/practice");
        await tab.Locator(ReadyIsland).WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        await network.WaitForFrameworkSettledAsync();

        RecordedRequest[] framework = [.. network.Framework];
        Assert.Contains(framework, r => app.Assets.Logical(r.Path) == "/_framework/dotnet.js");
        Assert.Contains(framework, r => r.Path.EndsWith(".wasm", StringComparison.Ordinal));
        AssertEveryFrameworkFileOnce(network);
        string[] development = [.. network.Requests.Select(r => r.Path).Where(p => p.Contains("HotReload", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".lib.module.js", StringComparison.Ordinal))];
        Assert.True(development.Length == 0, "The published build loads development scripts: " + string.Join(", ", development));
        // The islands never resolve assets, so the runtime must not wait for the asset collection (IslandEndpointConventions).
        Assert.DoesNotContain(framework, r => r.Path.StartsWith("/_framework/resource-collection", StringComparison.Ordinal));
        string[] unfingerprinted = [.. framework.Where(r => !app.Assets.IsFingerprinted(r.Path)).Select(r => r.Path)];
        Assert.True(unfingerprinted.Length == 0, "Requested without a fingerprint: " + string.Join(", ", unfingerprinted));
        app.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Island_NoCspViolations_AllFrameworkRequestsSucceed()
    {
        app.SkipUnlessAvailable();
        await using IBrowserContext context = await app.NewContextAsync();
        IPage tab = await app.NewPageAsync(context);
        NetworkRecorder network = await NetworkRecorder.AttachAsync(context, tab);
        List<string> consoleErrors = [];
        tab.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                consoleErrors.Add(message.Text);
            }
        };

        foreach (string path in new[] { "/en/dotnet/practice", "/ru/dotnet/mock" })
        {
            await tab.GotoAsync(path);
            await tab.Locator(ReadyIsland).WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
            await network.WaitForFrameworkSettledAsync();
        }

        Assert.NotEmpty(network.Framework);
        Assert.All(network.Framework, r =>
        {
            Assert.True(r.Failure is null, $"{r.Path} failed: {r.Failure}");
            Assert.True(r.Status == 200, $"{r.Path} answered {r.Status}.");
        });
        Assert.Empty(consoleErrors);
        app.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task SignedInWarmVisit_FrameworkFromHttpCache()
    {
        app.SkipUnlessAvailable();
        await using IBrowserContext context = await app.NewContextAsync();
        await app.SignInAsync(context, "Release Warm");
        IPage tab = await app.NewPageAsync(context);
        NetworkRecorder network = await NetworkRecorder.AttachAsync(context, tab);
        await tab.GotoAsync("/en/dotnet/practice");
        await tab.Locator(ReadyIsland).WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        await network.WaitForFrameworkSettledAsync();
        int cold = network.Framework.Count;
        await tab.GotoAsync("/en/about");

        network.Clear();
        await tab.GotoAsync("/en/dotnet/practice");
        await tab.Locator(ReadyIsland).WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        await network.WaitForFrameworkSettledAsync();

        RecordedRequest[] warm = [.. network.Framework];
        Assert.Equal(cold, warm.Length);
        string[] downloaded = [.. warm.Where(r => !r.FromCache).Select(r => $"{r.Path} ({r.Status})")];
        Assert.True(downloaded.Length == 0, "Not served from the browser cache on a warm visit: " + string.Join(", ", downloaded));
        Assert.Equal(1, await tab.EvaluateAsync<int>("() => window.__ditStarts || 0"));
        app.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Batch_CarriesDeviceUtcOffset()
    {
        app.SkipUnlessAvailable();
        await using IBrowserContext context = await app.NewContextAsync(timezoneId: "Asia/Novosibirsk");
        long telegramId = await app.SignInAsync(context, "Release Offset");
        IPage tab = await app.NewPageAsync(context);
        ApiTap batches = await ApiTap.AttachAsync(tab, "**/api/answers/batch");

        await tab.GotoAsync($"/en/dotnet/practice?first={SingleQuestionId}");
        await tab.Locator(".qcard").WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        await AnswerShownAsync(tab);

        ApiExchange batch = await batches.WaitForAsync(1);
        Assert.Equal("420", batch.RequestHeaders["x-client-utc-offset"]);
        Assert.Equal(telegramId.ToString(CultureInfo.InvariantCulture), batch.RequestHeaders["x-outbox-owner"]);
        await app.WaitForCountAsync(AnswerRowsSql, telegramId, 1);
        app.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task IslandHtml_HasImportMapWithRequestNonce_AndNoDevelopmentScripts()
    {
        app.SkipUnlessAvailable();
        using HttpClient client = new() { BaseAddress = app.BaseAddress };

        string[] nonces = new string[2];
        for (int i = 0; i < nonces.Length; i++)
        {
            using HttpResponseMessage response = await client.GetAsync(new Uri("/en/dotnet/practice", UriKind.Relative), Ct);
            string html = await response.Content.ReadAsStringAsync(Ct);
            string csp = string.Join("; ", response.Headers.GetValues("Content-Security-Policy"));
            // ADR 0012: an island page is never stored, so its nonce is never served twice (here anonymous).
            Assert.True(response.Headers.CacheControl?.NoStore == true, $"The anonymous island page is not no-store ({response.Headers.CacheControl}).");
            Match importMap = ImportMapScript().Match(html);
            Assert.True(importMap.Success, "The island page has no import map with a nonce.");
            nonces[i] = importMap.Groups["nonce"].Value;
            Assert.Contains($"'nonce-{nonces[i]}'", csp, StringComparison.Ordinal);
            using JsonDocument map = JsonDocument.Parse(WebUtility.HtmlDecode(importMap.Groups["map"].Value));
            string dotnet = map.RootElement.GetProperty("imports").GetProperty("./_framework/dotnet.js").GetString()!;
            Assert.True(app.Assets.IsFingerprinted("/" + dotnet.TrimStart('.', '/')), $"The import map points dotnet.js at {dotnet}.");

            // A Debug build adds the hot reload JS initializer and passes browser-tool variables in the WebAssembly options comment.
            Assert.Contains("<!--Blazor-WebAssembly:{\"environmentName\":\"Testing\",\"environmentVariables\":{}}-->", html, StringComparison.Ordinal);
            Assert.DoesNotContain("HotReload", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("browser-refresh", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("__ASPNETCORE_BROWSER_TOOLS", html, StringComparison.Ordinal);
            Assert.DoesNotContain(".lib.module.js", html, StringComparison.Ordinal);
        }

        Assert.NotEqual(nonces[0], nonces[1]);
    }

    [Fact]
    public async Task FingerprintedAssets_AreImmutable_AnonymousAndSignedIn()
    {
        app.SkipUnlessAvailable();
        await using IBrowserContext context = await app.NewContextAsync();
        await app.SignInAsync(context, "Release Assets");
        string session = (await context.CookiesAsync()).Single(c => c.Name == ".dit.session").Value;
        using HttpClient client = new() { BaseAddress = app.BaseAddress };

        using HttpRequestMessage pageRequest = new(HttpMethod.Get, "/en/dotnet/practice");
        pageRequest.Headers.Add("Cookie", ".dit.session=" + session);
        using HttpResponseMessage page = await client.SendAsync(pageRequest, Ct);
        string html = await page.Content.ReadAsStringAsync(Ct);
        // Only a signed-in page is private, no-store: the cookie was accepted.
        Assert.True(page.Headers.CacheControl is { Private: true, NoStore: true }, $"The page is not signed in (Cache-Control: {page.Headers.CacheControl}).");
        string[] assets = [.. AssetReference().Matches(html).Select(m => m.Groups["url"].Value).Distinct(StringComparer.Ordinal)];
        using JsonDocument map = JsonDocument.Parse(WebUtility.HtmlDecode(ImportMapScript().Match(html).Groups["map"].Value));
        assets = [.. assets, .. map.RootElement.GetProperty("imports").EnumerateObject().Select(p => "/" + p.Value.GetString()!.TrimStart('.', '/'))];
        string[] fingerprinted = [.. assets.Where(app.Assets.IsFingerprinted)];
        Assert.Contains(fingerprinted, a => a.StartsWith("/css/", StringComparison.Ordinal));
        Assert.Contains(fingerprinted, a => a.StartsWith("/js/", StringComparison.Ordinal));
        Assert.Contains(fingerprinted, a => a.StartsWith("/_framework/", StringComparison.Ordinal));

        foreach (string asset in fingerprinted)
        {
            foreach (string? cookie in new[] { null, ".dit.session=" + session })
            {
                using HttpRequestMessage request = new(HttpMethod.Get, asset);
                request.Headers.AcceptEncoding.ParseAdd("br, gzip");
                if (cookie is not null)
                {
                    request.Headers.Add("Cookie", cookie);
                }

                using HttpResponseMessage response = await client.SendAsync(request, Ct);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                string cacheControl = string.Join(", ", response.Headers.GetValues("Cache-Control"));
                string who = cookie is null ? "anonymous" : "signed in";
                Assert.True(cacheControl == "max-age=31536000, immutable", $"{asset} ({who}): Cache-Control is \"{cacheControl}\".");
                Assert.False(response.Headers.Contains("Set-Cookie"), $"{asset} ({who}) sets a cookie.");
            }
        }
    }

    private static Task PhaseAsync(IPage tab, params string[] phases) =>
        tab.Locator(string.Join(", ", phases.Select(p => $".mock[data-mock-phase='{p}']"))).First.WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });

    // Answers whatever question is on screen: the first option, the first line, some text or a self-grade of "knew".
    private static async Task AnswerShownAsync(IPage tab, string submit = ".practice__answer")
    {
        await tab.Locator(".qcard").WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        int saved = int.Parse(await tab.Locator(".qs").GetAttributeAsync("data-answers-saved") ?? "0", CultureInfo.InvariantCulture);
        if (await tab.Locator(".opt--pick").CountAsync() > 0)
        {
            await tab.Locator(".opt--pick").First.ClickAsync();
            await tab.Locator(submit).ClickAsync();
        }
        else if (await tab.Locator(".code-line").CountAsync() > 0)
        {
            await tab.Locator(".code-line").First.ClickAsync();
            await tab.Locator(submit).ClickAsync();
        }
        else if (await tab.Locator(".typed__input").CountAsync() > 0)
        {
            await tab.Locator(".typed__input").FillAsync("x");
            await tab.Locator(submit).ClickAsync();
        }
        else
        {
            await tab.Locator(".self > button.btn").ClickAsync();
            await tab.Locator(".self__grade--knew").ClickAsync();
        }

        await tab.Locator($".qs[data-answers-saved='{(saved + 1).ToString(CultureInfo.InvariantCulture)}']").WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        await tab.Locator(".feedback").WaitForAsync();
    }

    // Gives the mock question on screen a response and waits until the mock progress is saved.
    private static async Task GiveMockResponseAsync(IPage tab, string progressKey)
    {
        const string RevisionScript = "k => { const v = localStorage.getItem(k); return v ? JSON.parse(v).revision : 0; }";
        long before = await tab.EvaluateAsync<long>(RevisionScript, progressKey);
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
            await tab.Locator(".self__notes").FillAsync("notes");
        }

        await tab.WaitForFunctionAsync(
            "([k, r]) => { const v = localStorage.getItem(k); return v !== null && JSON.parse(v).revision > r; }",
            new object[] { progressKey, before },
            new PageWaitForFunctionOptions { Timeout = Wait });
    }

    /// <summary>
    /// Each runtime file is requested once, under one name: a fingerprinted preload followed by an unfingerprinted
    /// import of the same file (no import map) would download it twice.
    /// </summary>
    private void AssertEveryFrameworkFileOnce(NetworkRecorder network)
    {
        RecordedRequest[] framework = [.. network.Framework];
        Assert.NotEmpty(framework);
        string[] repeated = [.. framework
            .GroupBy(r => app.Assets.Logical(r.Path), StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(r => r.Path))}")];
        Assert.True(repeated.Length == 0, "Requested more than once: " + string.Join(" | ", repeated));
    }

    private static string Describe(IEnumerable<RecordedRequest> requests) =>
        string.Join(", ", requests.Select(r => r.Path + (r.FromCache ? " (cache)" : "")));

    private sealed record ApiExchange(string? RequestBody, IReadOnlyDictionary<string, string> RequestHeaders, HttpStatusCode Status, string ResponseBody);

    /// <summary>
    /// Records the exchanges of one API path through a route: Chromium may drop the body of a fetch response before the
    /// test reads it from the response event.
    /// </summary>
    private sealed class ApiTap
    {
        private readonly List<ApiExchange> _exchanges = [];

        public int Count
        {
            get
            {
                lock (_exchanges)
                {
                    return _exchanges.Count;
                }
            }
        }

        public IReadOnlyList<ApiExchange> Exchanges
        {
            get
            {
                lock (_exchanges)
                {
                    return [.. _exchanges];
                }
            }
        }

        public static async Task<ApiTap> AttachAsync(IPage tab, string glob)
        {
            ApiTap tap = new();
            await tab.RouteAsync(glob, tap.HandleAsync);
            return tap;
        }

        public async Task<ApiExchange> WaitForAsync(int count)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (Count < count)
            {
                Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(60), $"Exchange {count} did not happen.");
                await Task.Delay(50, Ct);
            }

            return Exchanges[count - 1];
        }

        private async Task HandleAsync(IRoute route)
        {
            IRequest request = route.Request;
            IAPIResponse response = await route.FetchAsync();
            string body = await response.TextAsync();
            lock (_exchanges)
            {
                _exchanges.Add(new ApiExchange(request.PostData, new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase), (HttpStatusCode)response.Status, body));
            }

            await route.FulfillAsync(new RouteFulfillOptions { Response = response, Body = body });
        }
    }
}
