using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Trainer.TestSupport;
using Trainer.Web.StaticSite;

namespace Trainer.Web.Tests;

/// <summary>The service worker's endpoint, its source text and its registration on the pages (ADR 0026).</summary>
public sealed class ServiceWorkerTests(StaticSiteFixture site, PostgresFixture postgres) : IClassFixture<StaticSiteFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Template() => File.ReadAllText(NodeHarness.WwwRootFile("js", "sw-template.js")).ReplaceLineEndings("\n");

    [Fact]
    public async Task ServiceWorker_ServedInStaticEditionOnly_WithTheSharedPattern()
    {
        using HttpClient client = site.Factory.CreateNonRedirectingClient();
        using HttpResponseMessage response = await client.GetAsync("/dit/sw.js", Ct);
        string body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith($"const ELIGIBLE = /{StaticAssetRules.JavaScriptRegex}/;", body, StringComparison.Ordinal);
        Assert.EndsWith(Template(), body, StringComparison.Ordinal);

        await using TrainerWebFactory server = await TrainerWebFactory.CreateAsync(postgres);
        await server.WaitUntilReadyAsync();
        using HttpClient serverClient = server.CreateNonRedirectingClient();
        using HttpResponseMessage missing = await serverClient.GetAsync("/sw.js", Ct);
        Assert.NotEqual(HttpStatusCode.OK, missing.StatusCode);
    }

    [Fact]
    public async Task ServiceWorker_TextIsStableBetweenRequests()
    {
        using HttpClient client = site.Factory.CreateNonRedirectingClient();
        string first = await client.GetStringAsync("/dit/sw.js", Ct);
        string second = await client.GetStringAsync("/dit/sw.js", Ct);

        Assert.Equal(first, second);
        Assert.DoesNotContain("\r", first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServiceWorker_SetsNoCookie()
    {
        using HttpClient client = site.Factory.CreateNonRedirectingClient();
        using HttpResponseMessage response = await client.GetAsync("/dit/sw.js", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public void Script_CacheNameCarriesTheScope_SkipWaitingAndClaim_PreloadAwaitedWithFallback_WritesBestEffort()
    {
        string script = Template();

        Assert.Contains("'dit-immutable-v1:' + SCOPE_PATH", script, StringComparison.Ordinal);
        Assert.Contains("new URL(self.registration.scope).pathname", script, StringComparison.Ordinal);
        Assert.Contains("self.skipWaiting()", script, StringComparison.Ordinal);
        Assert.Contains("self.clients.claim()", script, StringComparison.Ordinal);
        Assert.DoesNotContain("firstInstall", script, StringComparison.Ordinal);
        Assert.Contains("navigationPreload.enable()", script, StringComparison.Ordinal);
        Assert.Contains("await event.preloadResponse", script, StringComparison.Ordinal);
        Assert.Contains("return fetch(event.request)", script, StringComparison.Ordinal);
        Assert.Contains("ASSET_BUDGET_BYTES = 40 * 1024 * 1024", script, StringComparison.Ordinal);
        Assert.Contains("CONTENT_BUDGET_ENTRIES = 600", script, StringComparison.Ordinal);
        Assert.Contains("RECENCY_PERSIST_MS = 60 * 1000", script, StringComparison.Ordinal);

        // The write inside store() is wrapped in a try whose catch swallows the error.
        int store = script.IndexOf("async function store(", StringComparison.Ordinal);
        Assert.True(store >= 0);
        string body = script[store..script.IndexOf("async function cacheFirst(", StringComparison.Ordinal)];
        Assert.Contains("try {", body, StringComparison.Ordinal);
        Assert.Contains("catch (error)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_StoresOnlyOkNonRedirectedBasicResponsesForTheRequestedUrl()
    {
        string script = Template();
        int guard = script.IndexOf("function storable(", StringComparison.Ordinal);
        Assert.True(guard >= 0);
        string body = script[guard..script.IndexOf("function loadRecency(", StringComparison.Ordinal)];

        Assert.Contains("response.ok", body, StringComparison.Ordinal);
        Assert.Contains("!response.redirected", body, StringComparison.Ordinal);
        Assert.Contains("response.type === 'basic'", body, StringComparison.Ordinal);
        Assert.Contains("response.type === 'default'", body, StringComparison.Ordinal);
        Assert.Contains("response.url === requestedUrl", body, StringComparison.Ordinal);

        // Both writers of response bodies (a fetch and a fill) go through the guard.
        Assert.Equal(2, Regex.Count(script, @"storable\(response, url\)"));
        Assert.Equal(1, Regex.Count(script, @"cache\.put\(url, response\.clone\(\)\)"));
        Assert.Equal(1, Regex.Count(script, @"cache\.put\(url, copy\)"));
    }

    [Theory]
    [InlineData("/dit/en/")]
    [InlineData("/dit/ru/dotnet/practice/")]
    [InlineData("/dit/en/dotnet/questions/")]
    [InlineData("/dit/en/about/")]
    public async Task Registration_RenderedInStaticEditionOnly_WithThePrefixScope(string path)
    {
        using HttpClient client = site.Factory.CreateNonRedirectingClient();
        string html = await client.GetStringAsync(path, Ct);
        IHtmlDocument document = new HtmlParser().ParseDocument(html);

        IElement tag = Assert.Single(document.Scripts, s => (s.GetAttribute("src") ?? string.Empty).Contains("/js/sw-register", StringComparison.Ordinal));
        Assert.Matches(@"^/dit/js/sw-register\.[a-z0-9]{10}\.js$", tag.GetAttribute("src"));
        Assert.Equal("/dit/", tag.GetAttribute("data-scope"));
        Assert.Equal("/dit/sw.js", tag.GetAttribute("data-sw"));
        Assert.True(tag.HasAttribute("defer"));
    }

    [Fact]
    public async Task Registration_NotRenderedInServerEdition()
    {
        await using TrainerWebFactory server = await TrainerWebFactory.CreateAsync(postgres);
        await server.WaitUntilReadyAsync();
        using HttpClient client = server.CreateNonRedirectingClient();
        foreach (string path in new[] { "/en", "/en/dotnet/practice" })
        {
            IHtmlDocument document = new HtmlParser().ParseDocument(await client.GetStringAsync(path, Ct));

            // The import map of the island page names every asset, so only a script element counts.
            Assert.DoesNotContain(document.Scripts, s => (s.GetAttribute("src") ?? string.Empty).Contains("sw-register", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Registration_IsAllowedByTheMetaPolicy()
    {
        using HttpClient client = site.Factory.CreateNonRedirectingClient();
        string html = await client.GetStringAsync("/dit/en/dotnet/practice/", Ct);
        IHtmlDocument document = new HtmlParser().ParseDocument(html);
        string policy = document.QuerySelector("meta[http-equiv='Content-Security-Policy']")!.GetAttribute("content")!;

        Assert.Contains("worker-src 'self'", policy, StringComparison.Ordinal);
        Assert.Contains("script-src 'self'", policy, StringComparison.Ordinal);
    }
}
