using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Trainer.TestSupport;
using Trainer.Web.Analytics;
using Trainer.Web.Startup;

namespace Trainer.Web.Tests;

/// <summary>The static edition served with every setting this task reads switched on.</summary>
public sealed class StaticCspSiteFixture(PostgresFixture postgres) : IAsyncLifetime
{
    public const string PathBase = "/dit";

    public const string GoatCounterCode = "dit-test";

    public const string BuildId = "0123abc.99.1";

    public TrainerWebFactory Factory { get; private set; } = null!;

    public static Dictionary<string, string?> StaticSettings() => new(StringComparer.Ordinal)
    {
        ["App:Edition"] = "Static",
        ["App:PublicBaseUrl"] = "https://example.test" + PathBase + "/",
        ["App:Analytics:GoatCounterCode"] = GoatCounterCode,
        ["App:StaticSite:BuildId"] = BuildId,
        ["App:StaticSite:GoogleSiteVerification"] = "google-token-1",
        ["App:StaticSite:YandexVerification"] = "yandex-token-2",
    };

    public static async Task<TrainerWebFactory> CreateFactoryAsync(PostgresFixture postgres, Dictionary<string, string?> settings)
    {
        TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions { Settings = settings });
        await factory.WaitUntilReadyAsync();
        return factory;
    }

    public async ValueTask InitializeAsync() => Factory = await CreateFactoryAsync(postgres, StaticSettings());

    public async ValueTask DisposeAsync() => await Factory.DisposeAsync();
}

/// <summary>
/// The meta Content-Security-Policy and the other settings a static page carries instead of response headers
/// (ADR 0024).
/// </summary>
public sealed partial class StaticCspTests(StaticCspSiteFixture site, PostgresFixture postgres) : IClassFixture<StaticCspSiteFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Pages of every kind the export writes: public, island, chooser, home, not found.</summary>
    public static readonly string[] ExportedLikePages =
    [
        "/dit/",
        "/dit/en/",
        "/dit/ru/",
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

    [GeneratedRegex(@"script-src (?<sources>[^;]*);", RegexOptions.CultureInvariant)]
    private static partial Regex ScriptSrc();

    public static async Task<(HttpResponseMessage Response, string Body, IHtmlDocument Document)> FetchAsync(TrainerWebFactory factory, string path)
    {
        using HttpClient client = factory.CreateNonRedirectingClient();
        HttpResponseMessage response = await client.GetAsync(path, Ct);
        string body = await response.Content.ReadAsStringAsync(Ct);
        return (response, body, new HtmlParser().ParseDocument(body));
    }

    public static string CspOf(IHtmlDocument document) =>
        document.QuerySelector("meta[http-equiv=\"Content-Security-Policy\"]")?.GetAttribute("content")
        ?? throw new InvalidOperationException("The page has no CSP meta element.");

    [Theory]
    [InlineData("/dit/")]
    [InlineData("/dit/en/dotnet/questions/")]
    [InlineData("/dit/en/dotnet/practice/")]
    [InlineData("/dit/zz/nothing/")]
    public async Task PublicPage_CspMetaIsTheFirstHeadElementBeforeTheThemeScript(string path)
    {
        (HttpResponseMessage response, _, IHtmlDocument document) = await FetchAsync(site.Factory, path);
        using HttpResponseMessage disposable = response;

        IElement first = Assert.IsAssignableFrom<IElement>(document.Head!.FirstElementChild);
        Assert.Equal("meta", first.LocalName);
        Assert.Equal("Content-Security-Policy", first.GetAttribute("http-equiv"));

        // The chooser has no theme script; every other page has it, and the policy must come before all scripts.
        IElement? firstScript = document.Head.QuerySelector("script");
        Assert.NotNull(firstScript);
        Assert.True(first.CompareDocumentPosition(firstScript).HasFlag(DocumentPositions.Following));
        if (path != "/dit/")
        {
            Assert.Equal(ThemeScript.StaticSource, firstScript.TextContent);
        }
    }

    [Fact]
    public async Task IslandPage_CspAllowsItsImportMapByHashAndWasmAndBaseSelf()
    {
        (HttpResponseMessage response, _, IHtmlDocument document) = await FetchAsync(site.Factory, "/dit/en/dotnet/practice/");
        using HttpResponseMessage disposable = response;

        string csp = CspOf(document);
        IElement importMap = Assert.IsAssignableFrom<IElement>(document.QuerySelector("script[type=\"importmap\"]"));
        Assert.False(importMap.HasAttribute("nonce"), "A static file cannot carry a nonce.");
        string sources = ScriptSrc().Match(csp).Groups["sources"].Value;
        Assert.Contains("'wasm-unsafe-eval'", sources, StringComparison.Ordinal);
        Assert.Contains(StaticCsp.Sha256Source(importMap.TextContent), sources, StringComparison.Ordinal);
        Assert.Contains("base-uri 'self'", csp, StringComparison.Ordinal);
        Assert.NotNull(document.QuerySelector("base[href=\"/dit/\"]"));
    }

    [Fact]
    public async Task PublicPage_CspHasBaseUriNoneAndNoBaseElement()
    {
        (HttpResponseMessage response, _, IHtmlDocument document) = await FetchAsync(site.Factory, "/dit/en/dotnet/questions/");
        using HttpResponseMessage disposable = response;

        string csp = CspOf(document);
        Assert.Contains("base-uri 'none'", csp, StringComparison.Ordinal);
        Assert.Null(document.QuerySelector("base"));
        Assert.DoesNotContain("wasm-unsafe-eval", csp, StringComparison.Ordinal);
        Assert.Null(document.QuerySelector("script[type=\"importmap\"]"));
        Assert.Equal("strict-origin-when-cross-origin", document.QuerySelector("meta[name=\"referrer\"]")?.GetAttribute("content"));
    }

    [Theory]
    [InlineData("/dit/")]
    [InlineData("/dit/en/dotnet/questions/")]
    [InlineData("/dit/en/dotnet/practice/")]
    [InlineData("/dit/zz/nothing/")]
    public async Task StaticEdition_SendsNoSecurityHeaders(string path)
    {
        (HttpResponseMessage response, _, _) = await FetchAsync(site.Factory, path);
        using HttpResponseMessage disposable = response;

        Assert.False(response.Headers.Contains("Content-Security-Policy"));
        Assert.False(response.Headers.Contains("X-Content-Type-Options"));
        Assert.False(response.Headers.Contains("Referrer-Policy"));
        Assert.False(response.Content.Headers.Contains("Content-Security-Policy"));
    }

    [Theory]
    [InlineData("/dit/")]
    [InlineData("/dit/en/dotnet/practice/")]
    public async Task CspMeta_HasNoFrameAncestors(string path)
    {
        (HttpResponseMessage response, _, IHtmlDocument document) = await FetchAsync(site.Factory, path);
        using HttpResponseMessage disposable = response;

        string csp = CspOf(document);
        foreach (string unsupported in new[] { "frame-ancestors", "report-uri", "report-to", "sandbox" })
        {
            Assert.DoesNotContain(unsupported, csp, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CspMeta_AllowsGoatCounterOnlyForTheConfiguredCode()
    {
        (HttpResponseMessage response, _, IHtmlDocument document) = await FetchAsync(site.Factory, "/dit/en/dotnet/questions/");
        using HttpResponseMessage disposable = response;

        string csp = CspOf(document);
        Assert.DoesNotContain("zgo.at", csp, StringComparison.Ordinal);
        Assert.Contains("connect-src 'self' https://dit-test.goatcounter.com;", csp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CspMeta_WithoutGoatCounterCode_NamesNoThirdPartyOrigin()
    {
        Dictionary<string, string?> settings = StaticCspSiteFixture.StaticSettings();
        settings.Remove("App:Analytics:GoatCounterCode");
        await using TrainerWebFactory factory = await StaticCspSiteFixture.CreateFactoryAsync(postgres, settings);

        (HttpResponseMessage response, string body, IHtmlDocument document) = await FetchAsync(factory, "/dit/en/dotnet/questions/");
        using HttpResponseMessage disposable = response;

        string csp = CspOf(document);
        Assert.DoesNotContain("goatcounter", csp, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("zgo.at", csp, StringComparison.Ordinal);
        Assert.Contains("connect-src 'self';", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("goatcounter", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EveryStaticPage_CarriesTheBuildIdMeta()
    {
        foreach (string path in ExportedLikePages)
        {
            (HttpResponseMessage response, _, IHtmlDocument document) = await FetchAsync(site.Factory, path);
            using HttpResponseMessage disposable = response;

            Assert.Equal(StaticCspSiteFixture.BuildId, document.QuerySelector("meta[name=\"dit-build\"]")?.GetAttribute("content"));
        }
    }

    [Fact]
    public async Task VerificationMeta_OnRootAndHomePagesWhenConfigured()
    {
        foreach (string home in new[] { "/dit/", "/dit/en/", "/dit/ru/" })
        {
            (HttpResponseMessage response, _, IHtmlDocument document) = await FetchAsync(site.Factory, home);
            using HttpResponseMessage disposable = response;

            Assert.Equal("google-token-1", document.QuerySelector("meta[name=\"google-site-verification\"]")?.GetAttribute("content"));
            Assert.Equal("yandex-token-2", document.QuerySelector("meta[name=\"yandex-verification\"]")?.GetAttribute("content"));
        }

        foreach (string other in new[] { "/dit/en/dotnet/questions/closure-for-loop/", "/dit/en/about/", "/dit/en/dotnet/practice/", "/dit/zz/nothing/" })
        {
            (HttpResponseMessage response, _, IHtmlDocument document) = await FetchAsync(site.Factory, other);
            using HttpResponseMessage disposable = response;

            Assert.Null(document.QuerySelector("meta[name=\"google-site-verification\"]"));
            Assert.Null(document.QuerySelector("meta[name=\"yandex-verification\"]"));
        }

        Dictionary<string, string?> unset = StaticCspSiteFixture.StaticSettings();
        unset.Remove("App:StaticSite:GoogleSiteVerification");
        unset.Remove("App:StaticSite:YandexVerification");
        await using TrainerWebFactory factory = await StaticCspSiteFixture.CreateFactoryAsync(postgres, unset);
        foreach (string home in new[] { "/dit/", "/dit/en/" })
        {
            (HttpResponseMessage response, _, IHtmlDocument document) = await FetchAsync(factory, home);
            using HttpResponseMessage disposable = response;

            Assert.Null(document.QuerySelector("meta[name=\"google-site-verification\"]"));
            Assert.Null(document.QuerySelector("meta[name=\"yandex-verification\"]"));
        }
    }

    [Fact]
    public async Task EveryStaticPage_EveryInlineScriptIsAllowedByHash()
    {
        foreach (string path in ExportedLikePages)
        {
            (HttpResponseMessage response, _, IHtmlDocument document) = await FetchAsync(site.Factory, path);
            using HttpResponseMessage disposable = response;
            Assert.True(
                response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound,
                $"{path}: unexpected status {(int)response.StatusCode}");

            string sources = ScriptSrc().Match(CspOf(document)).Groups["sources"].Value;
            // Only scripts a browser executes or reads as an import map are governed by script-src; a JSON-LD block is data.
            IElement[] inline = [.. document.QuerySelectorAll("script").Where(s =>
                !s.HasAttribute("src") && s.GetAttribute("type") is null or "" or "module" or "importmap")];
            Assert.NotEmpty(inline);
            Assert.All(document.QuerySelectorAll("script:not([src])"), s => Assert.Contains(s.GetAttribute("type"), new string?[] { null, "", "importmap", "application/ld+json" }));
            foreach (IElement script in inline)
            {
                string hash = StaticCsp.Sha256Source(script.TextContent);
                string preview = script.TextContent[..Math.Min(60, script.TextContent.Length)];
                Assert.True(sources.Contains(hash, StringComparison.Ordinal), $"{path}: inline script {hash} is not allowed by script-src: {preview}");
            }

            Assert.Empty(document.QuerySelectorAll("[onclick], [onload], [onerror], [style]"));
        }
    }

    [Fact]
    public async Task EveryStaticPage_EveryExternalScriptIsSameOrigin()
    {
        foreach (string path in ExportedLikePages)
        {
            (HttpResponseMessage response, _, IHtmlDocument document) = await FetchAsync(site.Factory, path);
            using HttpResponseMessage disposable = response;

            foreach (IElement script in document.QuerySelectorAll("script[src]"))
            {
                string src = script.GetAttribute("src")!;
                Assert.True(
                    src.StartsWith("/dit/", StringComparison.Ordinal),
                    $"{path}: script {src} is not same-origin");
            }
        }
    }

    [Fact]
    public async Task CharsetMeta_StillLiesWithinTheFirstKilobyte()
    {
        foreach (string path in new[] { "/dit/", "/dit/en/", "/dit/en/dotnet/practice/" })
        {
            (HttpResponseMessage response, string body, _) = await FetchAsync(site.Factory, path);
            using HttpResponseMessage disposable = response;

            int charset = body.IndexOf("<meta charset", StringComparison.Ordinal);
            Assert.True(charset >= 0 && Encoding.UTF8.GetByteCount(body[..charset]) + 20 <= 1024, $"{path}: charset meta at {charset}");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("dit-test")]
    public void For_PublicPolicy_HasTheSpecifiedShape(string? code)
    {
        string csp = StaticCsp.For(false, null, code);

        string theme = StaticCsp.Sha256Source(ThemeScript.StaticSource);
        string expectedScripts = $"'self' {theme} {RecoveryScript.CspSource}";
        string expectedConnect = code is null ? "'self'" : "'self' https://dit-test.goatcounter.com";
        Assert.Equal(
            $"default-src 'self'; script-src {expectedScripts}; style-src 'self'; img-src 'self' https: data:; font-src 'self'; " +
            $"connect-src {expectedConnect}; worker-src 'self'; base-uri 'none'; form-action 'self'",
            csp);
    }

    [Fact]
    public void For_IslandPolicy_AddsWasmAndTheImportMapHashAndSelfBase()
    {
        string map = StaticCsp.Sha256Source("{\"imports\":{}}");

        string csp = StaticCsp.For(true, map, null);

        Assert.Contains($"script-src 'self' 'wasm-unsafe-eval' {map} ", csp, StringComparison.Ordinal);
        Assert.Contains("base-uri 'self';", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("zgo.at", csp, StringComparison.Ordinal);
    }

    [Fact]
    public void For_IslandWithoutImportMapHash_Throws()
    {
        Assert.Throws<ArgumentException>(() => StaticCsp.For(true, null, null));
    }

    [Theory]
    [InlineData("a; script-src *")]
    [InlineData("UPPER")]
    [InlineData("x y")]
    public void For_MalformedGoatCounterCode_Throws(string code)
    {
        Assert.Throws<ArgumentException>(() => StaticCsp.For(false, null, code));
    }

    [Fact]
    public void Sha256Source_IsTheBase64Sha256OfTheUtf8Text()
    {
        // The value a browser computes for the script text "alert(1)".
        Assert.Equal("'sha256-bhHHL3z2vDgxUt0W3dWQOrprscmda2Y5pLsLg4GF+pI='", StaticCsp.Sha256Source("alert(1)"));
    }
}
