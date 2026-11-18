using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Trainer.TestSupport;
using Trainer.Web.StaticSite;

namespace Trainer.Web.Tests;

/// <summary>
/// The chrome, routing and access rules of the static edition (ADR 0022): a language chooser at the root, no server
/// endpoints, Review and Profile open without a sign-in, and no cookies.
/// </summary>
public sealed partial class StaticEditionPageTests(StaticSiteFixture site, PostgresFixture postgres) : IClassFixture<StaticSiteFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("<!--Blazor-WebAssembly-Component-State:(?<state>[A-Za-z0-9+/=]+)-->", RegexOptions.CultureInvariant)]
    private static partial Regex PersistedState();

    private static async Task<(HttpResponseMessage Response, string Body)> GetAsync(TrainerWebFactory factory, string path)
    {
        using HttpClient client = factory.CreateNonRedirectingClient();
        HttpResponseMessage response = await client.GetAsync(path, Ct);
        return (response, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<IHtmlDocument> DocumentAsync(string path)
    {
        (HttpResponseMessage response, string body) = await GetAsync(site.Factory, path);
        using HttpResponseMessage _ = response;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return new HtmlParser().ParseDocument(body);
    }

    [Theory]
    [InlineData("/dit/en/dotnet/questions", "Progress")]
    [InlineData("/dit/ru/dotnet/questions", "Прогресс")]
    public async Task Header_HasProgressLinkAndNoSignIn(string page, string label)
    {
        IHtmlDocument document = await DocumentAsync(page);

        string culture = page.Contains("/ru/", StringComparison.Ordinal) ? "ru" : "en";
        IElement? progress = document.QuerySelector($"header a[href=\"/dit/{culture}/dotnet/profile/\"]");
        Assert.NotNull(progress);
        Assert.Equal(label, progress.TextContent.Trim());
        Assert.Null(document.QuerySelector("header form"));
        Assert.Empty(document.QuerySelectorAll("header a[href*=\"signin\"]"));
        Assert.Null(document.QuerySelector(".import-notice"));
    }

    [Fact]
    public async Task LanguageSwitcher_LinksDirectlyToTheSamePageInTheOtherLanguage()
    {
        IHtmlDocument document = await DocumentAsync("/dit/en/dotnet/questions/");

        IElement? link = document.QuerySelector("a[href=\"/dit/ru/dotnet/questions/\"][data-lang-choice=\"ru\"]");
        Assert.NotNull(link);
        Assert.Equal("ru", link.GetAttribute("hreflang"));
        Assert.Equal("Русский", link.TextContent.Trim());
        Assert.Empty(document.QuerySelectorAll("a[href*=\"/lang/\"]"));
        Assert.Contains(document.Scripts, s => Regex.IsMatch(s.Source ?? string.Empty, @"^/dit/js/lang-choice(\.[a-z0-9]+)?\.js$", RegexOptions.CultureInvariant));
    }

    [Fact]
    public async Task Root_ServesTheLanguageChooserWithBothLinks()
    {
        (HttpResponseMessage response, string body) = await GetAsync(site.Factory, "/dit/");
        using HttpResponseMessage _ = response;
        IHtmlDocument document = new HtmlParser().ParseDocument(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("en", document.DocumentElement.GetAttribute("lang"));
        Assert.Equal("noindex", document.QuerySelector("meta[name=\"robots\"]")?.GetAttribute("content"));
        IElement? ru = document.QuerySelector("a[href=\"/dit/ru/\"]");
        IElement? en = document.QuerySelector("a[href=\"/dit/en/\"]");
        Assert.Equal("ru", ru?.GetAttribute("lang"));
        Assert.Equal("ru", ru?.GetAttribute("hreflang"));
        Assert.Equal("en", en?.GetAttribute("lang"));
        Assert.Equal("en", en?.GetAttribute("hreflang"));
        Assert.Equal("Русский", ru?.TextContent.Trim());
        Assert.Equal("English", en?.TextContent.Trim());
        Assert.Equal("Dev Interview Trainer", document.Title);

        // A shared root link gets a preview: the English site card, the same one /en/ advertises.
        (HttpResponseMessage enResponse, string enBody) = await GetAsync(site.Factory, "/dit/en/");
        using HttpResponseMessage __ = enResponse;
        IHtmlDocument enHome = new HtmlParser().ParseDocument(enBody);
        string? Og(IHtmlDocument d, string property) => d.QuerySelector($"meta[property=\"og:{property}\"]")?.GetAttribute("content");
        Assert.Equal(Og(enHome, "image"), Og(document, "image"));
        Assert.Equal(Og(enHome, "title"), Og(document, "title"));
        Assert.Equal(Og(enHome, "description"), Og(document, "description"));
        Assert.EndsWith("/dit/", Og(document, "url"), StringComparison.Ordinal);
        Assert.Equal("en_US", Og(document, "locale"));
        Assert.Equal("summary_large_image", document.QuerySelector("meta[name=\"twitter:card\"]")?.GetAttribute("content"));
        IElement? script = document.QuerySelector("script[data-base]");
        Assert.NotNull(script);
        Assert.Equal("/dit/", script.GetAttribute("data-base"));
        // Unfingerprinted on purpose: the chooser is a minimal endpoint, which has no asset collection to fingerprint from.
        Assert.Equal("/dit/js/root-chooser.js", script.GetAttribute("src"));
        Assert.Equal("Language", document.QuerySelector("nav")?.GetAttribute("aria-label"));
        Assert.Empty(response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies) ? cookies : []);
    }

    [Fact]
    public async Task LangEndpoint_IsNotMappedInStaticEdition()
    {
        (HttpResponseMessage response, _) = await GetAsync(site.Factory, "/dit/lang/en?return=/en");
        using HttpResponseMessage _1 = response;

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData("review")]
    [InlineData("profile")]
    public async Task ProgressPages_OpenForAnonymousVisitors(string page)
    {
        (HttpResponseMessage response, string body) = await GetAsync(site.Factory, $"/dit/en/dotnet/{page}/");
        using HttpResponseMessage _ = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Contains($"<div class=\"island\" data-island=\"{page}\">", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en", "Report a problem")]
    [InlineData("ru", "Сообщить о проблеме")]
    public async Task StaticQuestionPage_ShowsReportLinkToEveryVisitorAndNoRatingForms(string culture, string label)
    {
        IHtmlDocument document = await DocumentAsync($"/dit/{culture}/dotnet/questions/closure-for-loop/");

        IElement? link = document.QuerySelector("a[href^=\"https://github.com/Platonenkov/dev-interview-trainer/issues/new?\"]");
        Assert.NotNull(link);
        Assert.Equal(label, link.TextContent.Trim());
        System.Collections.Specialized.NameValueCollection query = System.Web.HttpUtility.ParseQueryString(new Uri(link.GetAttribute("href")!).Query);
        Assert.Equal("question-report.yml", query["template"]);
        Assert.Equal("01KBZ3Q8N9V2K3M4P5R6S7T8WA", query["question-id"]);
        Assert.Equal(culture, query["language"]);
        Assert.Equal($"https://example.test/dit/{culture}/dotnet/questions/closure-for-loop/", query["page-url"]);
        Assert.Equal($"[{culture}] closure-for-loop", query["title"]);
        Assert.Contains("noopener", link.GetAttribute("rel"), StringComparison.Ordinal);
        Assert.Null(document.QuerySelector("#feedback"));
        Assert.Empty(document.QuerySelectorAll("form"));
        Assert.Empty(document.QuerySelectorAll("[name=\"rating\"], [aria-pressed]"));
    }

    [Fact]
    public async Task EveryExportedKindOfResponse_SetsNoCookie()
    {
        string[] paths =
        [
            "/dit/",
            "/dit/en",
            "/dit/ru/dotnet/questions/",
            "/dit/en/dotnet/questions/closure-for-loop/",
            "/dit/en/dotnet/topics/async/",
            "/dit/en/about/",
            "/dit/en/dotnet/practice/",
            "/dit/ru/dotnet/mock/",
            "/dit/en/dotnet/review/",
            "/dit/en/dotnet/profile/",
            "/dit/en/dotnet/questions/no-such-question/",
            "/dit/zz/nothing/",
        ];

        foreach (string path in paths)
        {
            (HttpResponseMessage response, _) = await GetAsync(site.Factory, path);
            using HttpResponseMessage _1 = response;
            Assert.False(response.Headers.Contains("Set-Cookie"), $"{path}: {(int)response.StatusCode} set a cookie");
        }
    }

    private static string PersistedRegistry(string body)
    {
        Match state = PersistedState().Match(body);
        Assert.True(state.Success, "An island page persists component state.");
        using JsonDocument persisted = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(state.Groups["state"].Value)));
        return Encoding.UTF8.GetString(Convert.FromBase64String(
            persisted.RootElement.GetProperty("Microsoft.AspNetCore.Components.Infrastructure.PersistentServicesRegistry").GetString()!));
    }

    [Fact]
    public async Task IslandPage_PersistsNoAntiforgeryToken()
    {
        (HttpResponseMessage response, string body) = await GetAsync(site.Factory, "/dit/en/dotnet/review/");
        using HttpResponseMessage _ = response;

        Assert.DoesNotContain("Antiforgery", PersistedRegistry(body), StringComparison.Ordinal);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies)
            && cookies.Any(c => c.StartsWith(".dit.af", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task IslandPage_ImportMapNamesAFingerprintedBrotliDecoder()
    {
        IHtmlDocument document = await DocumentAsync("/dit/en/dotnet/practice/");

        string map = document.QuerySelector("script[type=\"importmap\"]")!.TextContent;
        using JsonDocument json = JsonDocument.Parse(map);
        JsonProperty decoder = Assert.Single(
            json.RootElement.GetProperty("imports").EnumerateObject(),
            p => p.Name.EndsWith("js/vendor/brotli-decode.mjs", StringComparison.Ordinal));
        string target = decoder.Value.GetString()!;

        // import('./vendor/brotli-decode.mjs') resolves to the key; the map turns it into the content-addressed route,
        // which the worker's rule covers, so the decoder is cached like every other fingerprinted file.
        Assert.Matches(@"js/vendor/brotli-decode\.[a-z0-9]{10}\.mjs$", target);
        Assert.True(StaticAssetRules.IsEligible(target[(target.IndexOf("js/vendor", StringComparison.Ordinal))..]), target);
    }

    [Fact]
    public async Task IslandPage_ImportMapCoversTheBootIntegrityTable_OnlyWhenTheRuntimeIsBrotli()
    {
        using (JsonDocument off = JsonDocument.Parse((await DocumentAsync("/dit/en/dotnet/practice/")).QuerySelector("script[type=\"importmap\"]")!.TextContent))
        {
            Assert.DoesNotContain(off.RootElement.GetProperty("imports").EnumerateObject(), p => p.Name.Contains("boot-integrity", StringComparison.Ordinal));
        }

        await using TrainerWebFactory brotli = await TrainerWebFactory.CreateAsync(
            postgres,
            new TrainerWebFactoryOptions
            {
                Settings = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["App:Edition"] = "Static",
                    ["App:PublicBaseUrl"] = StaticSiteFixture.Origin + StaticSiteFixture.PathBase + "/",
                    ["App:StaticSite:BrotliRuntime"] = "true",
                },
            });
        await brotli.WaitUntilReadyAsync();
        (HttpResponseMessage page, string html) = await GetAsync(brotli, "/dit/en/dotnet/practice/");
        using HttpResponseMessage _ = page;
        string mapText = new HtmlParser().ParseDocument(html).QuerySelector("script[type=\"importmap\"]")!.TextContent;
        using JsonDocument map = JsonDocument.Parse(mapText);

        // import('./boot-integrity.mjs') from islands-start.js resolves to the key; the map turns it into the content-addressed file.
        JsonProperty key = Assert.Single(
            map.RootElement.GetProperty("imports").EnumerateObject(),
            p => p.Name.EndsWith("js/boot-integrity.mjs", StringComparison.Ordinal));
        string target = key.Value.GetString()!;
        Assert.Matches(@"js/boot-integrity\.[a-z0-9]{10}\.mjs$", target);

        // The table is the root of trust for the decoded runtime, so the browser checks it before running it.
        string integrity = map.RootElement.GetProperty("integrity").GetProperty(target).GetString()!;
        (HttpResponseMessage file, string body) = await GetAsync(brotli, "/dit/" + target[target.IndexOf("js/", StringComparison.Ordinal)..]);
        using HttpResponseMessage __ = file;
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("sha256-" + Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(body))), integrity);
        Assert.Equal("text/javascript", file.Content.Headers.ContentType?.MediaType);
        Assert.Matches("^export default \\{\"[^\"]+\\.wasm\":\"sha256-[A-Za-z0-9+/]+={0,2}\"", body);
    }

    [Fact]
    public async Task IslandPage_AsksForTheBrotliLoaderOnlyWhenTheRuntimeIsBrotli()
    {
        IHtmlDocument off = await DocumentAsync("/dit/en/dotnet/practice/");
        Assert.False(off.QuerySelector("script[src*=\"islands-start\"]")!.HasAttribute("data-brotli"));

        await using TrainerWebFactory brotli = await TrainerWebFactory.CreateAsync(
            postgres,
            new TrainerWebFactoryOptions
            {
                Settings = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["App:Edition"] = "Static",
                    ["App:PublicBaseUrl"] = StaticSiteFixture.Origin + StaticSiteFixture.PathBase + "/",
                    ["App:StaticSite:BrotliRuntime"] = "true",
                },
            });
        await brotli.WaitUntilReadyAsync();
        (HttpResponseMessage response, string body) = await GetAsync(brotli, "/dit/en/dotnet/practice/");
        using HttpResponseMessage _ = response;

        IElement start = new HtmlParser().ParseDocument(body).QuerySelector("script[src*=\"islands-start\"]")!;
        Assert.Equal("true", start.GetAttribute("data-brotli"));
    }

    // Positive control for the static antiforgery test: without it a renamed registry key or type would make that
    // test pass vacuously.
    [Fact]
    public async Task ServerEdition_IslandPage_StillPersistsTheAntiforgeryToken()
    {
        await using TrainerWebFactory server = await TrainerWebFactory.CreateAsync(postgres);
        await server.WaitUntilReadyAsync();

        (HttpResponseMessage response, string body) = await GetAsync(server, "/en/dotnet/practice");
        using HttpResponseMessage _ = response;

        Assert.Contains("Antiforgery", PersistedRegistry(body), StringComparison.Ordinal);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(".dit.af", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/dit/en/dotnet/", "What you missed comes back tomorrow, what you knew comes back in a week. Your progress stays in this browser.")]
    [InlineData("/dit/ru/dotnet/", "Что не знал, вернётся завтра, что знал, через неделю. Прогресс хранится в этом браузере.")]
    public async Task HomeReviewCard_ExplainsLocalProgress(string page, string expected)
    {
        IHtmlDocument document = await DocumentAsync(page);

        IElement? card = document.QuerySelector("a.mode-card[href$=\"/review/\"]");
        Assert.NotNull(card);
        Assert.Equal(expected, card.QuerySelector("p")?.TextContent.Trim());
        Assert.Single(card.QuerySelectorAll("p"));
        Assert.Null(card.QuerySelector(".mode-card__action"));
    }

    [Theory]
    [InlineData("/dit/en/about/", "This edition of the site", "static edition of the trainer")]
    [InlineData("/dit/ru/about/", "Эта версия сайта", "статическая версия тренажёра")]
    public async Task About_HasTheStaticEditionParagraph(string page, string heading, string textStart)
    {
        IHtmlDocument document = await DocumentAsync(page);

        IElement? title = document.QuerySelectorAll("article h2").FirstOrDefault(h => h.TextContent.Trim() == heading);
        Assert.NotNull(title);
        Assert.Contains(textStart, title.NextElementSibling?.TextContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/dit/en/about/", new[] { "lang cookie", "self-hosted", "account", "sign-in cookie", "own server", "GoatCounter" }, new[] { "no cookies", "Report a problem", "GitHub", "No analytics run" })]
    [InlineData("/dit/ru/about/", new[] { "lang", "аккаунт", "собственный счётчик", "на сервере самого сайта", "cookie входа", "GoatCounter" }, new[] { "не устанавливает cookie", "Сообщить о проблеме", "GitHub", "аналитика не используется" })]
    public async Task About_InTheStaticEdition_MakesNoServerClaims_AndNoAnalyticsClaimWithoutACode(string page, string[] serverClaims, string[] staticClaims)
    {
        IHtmlDocument document = await DocumentAsync(page);

        string text = document.QuerySelector("article")!.TextContent;
        foreach (string claim in serverClaims)
        {
            Assert.DoesNotContain(claim, text, StringComparison.OrdinalIgnoreCase);
        }

        foreach (string claim in staticClaims)
        {
            Assert.Contains(claim, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("/dit/en/about/", "Page views are counted by GoatCounter", "No analytics run")]
    [InlineData("/dit/ru/about/", "Просмотры страниц считает GoatCounter", "аналитика не используется")]
    public async Task About_InTheStaticEdition_NamesGoatCounterWhenACodeIsConfigured(string page, string counted, string noAnalytics)
    {
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(
            postgres,
            new TrainerWebFactoryOptions
            {
                Settings = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["App:Edition"] = "Static",
                    ["App:PublicBaseUrl"] = StaticSiteFixture.Origin + StaticSiteFixture.PathBase + "/",
                    ["App:Analytics:GoatCounterCode"] = "dit-test",
                },
            });
        await factory.WaitUntilReadyAsync();

        (HttpResponseMessage response, string body) = await GetAsync(factory, page);
        using HttpResponseMessage _ = response;
        string text = new HtmlParser().ParseDocument(body).QuerySelector("article")!.TextContent;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(counted, text, StringComparison.Ordinal);
        Assert.DoesNotContain(noAnalytics, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ServerEdition_ReviewStillRedirectsAnonymousToSignIn()
    {
        await using TrainerWebFactory server = await TrainerWebFactory.CreateAsync(postgres);
        await server.WaitUntilReadyAsync();

        (HttpResponseMessage response, _) = await GetAsync(server, "/en/dotnet/review");
        using HttpResponseMessage _1 = response;

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/signin?ReturnUrl=%2Fen%2Fdotnet%2Freview", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task ServerEdition_RootStillRedirectsAndLangStillSetsTheCookie()
    {
        await using TrainerWebFactory server = await TrainerWebFactory.CreateAsync(postgres);
        await server.WaitUntilReadyAsync();

        (HttpResponseMessage root, _) = await GetAsync(server, "/");
        (HttpResponseMessage lang, _) = await GetAsync(server, "/lang/ru?return=/ru");
        using HttpResponseMessage _1 = root;
        using HttpResponseMessage _2 = lang;

        Assert.Equal(HttpStatusCode.Redirect, root.StatusCode);
        Assert.Equal("/en", root.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, lang.StatusCode);
        Assert.Contains(lang.Headers.GetValues("Set-Cookie"), c => c.StartsWith("lang=ru", StringComparison.Ordinal));
    }
}
