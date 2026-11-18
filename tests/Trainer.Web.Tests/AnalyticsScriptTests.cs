using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Microsoft.Extensions.Configuration;
using Trainer.TestSupport;
using Trainer.Web.Analytics;

namespace Trainer.Web.Tests;

public sealed class AnalyticsScriptTests(PostgresFixture postgres)
{
    private const string WebsiteId = "3f1d2c4e-5a6b-4c7d-8e9f-0a1b2c3d4e5f";
    private const string ScriptTag = "src=\"/stats/script.js\"";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<TrainerWebFactory> CreateAsync(string? websiteId)
    {
        Dictionary<string, string?> settings = [];
        if (websiteId is not null)
        {
            settings["App:Analytics:WebsiteId"] = websiteId;
        }

        TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions { Settings = settings });
        await factory.WaitUntilReadyAsync();
        return factory;
    }

    private static async Task<(HttpResponseMessage Response, string Body)> GetAsync(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.GetAsync(path, Ct);
        return (response, await response.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task NoWebsiteId_RendersNoScript(string? websiteId)
    {
        await using TrainerWebFactory factory = await CreateAsync(websiteId);
        using HttpClient client = factory.CreateNonRedirectingClient();

        foreach (string path in new[] { "/en", "/en/about", "/ru/signin", "/en/dotnet/practice" })
        {
            (HttpResponseMessage response, string body) = await GetAsync(client, path);
            using HttpResponseMessage disposable = response;

            Assert.DoesNotContain("/stats", body, StringComparison.Ordinal);
            Assert.DoesNotContain("data-website-id", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task PublicPage_RendersScriptWithExcludeSearchAndHostUrl()
    {
        await using TrainerWebFactory factory = await CreateAsync(WebsiteId);
        using HttpClient client = factory.CreateNonRedirectingClient();

        (HttpResponseMessage response, string body) = await GetAsync(client, "/en/dotnet/questions");
        using HttpResponseMessage disposable = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "<script defer " + ScriptTag + " data-website-id=\"" + WebsiteId + "\" data-host-url=\"https://trainer.test/stats\"",
            body,
            StringComparison.Ordinal);
        Assert.Contains("data-do-not-track=\"true\"", body, StringComparison.Ordinal);
        Assert.Contains("data-exclude-search=\"true\"", body, StringComparison.Ordinal);
        Assert.Contains("data-exclude-hash=\"true\"", body, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, System.Text.RegularExpressions.Regex.Escape(ScriptTag)));
    }

    [Fact]
    public async Task AnonymousPageWithScript_StillNoSetCookieAndCached()
    {
        await using TrainerWebFactory factory = await CreateAsync(WebsiteId);
        using HttpClient client = factory.CreateNonRedirectingClient();

        (HttpResponseMessage first, string firstBody) = await GetAsync(client, "/en/about");
        (HttpResponseMessage second, string secondBody) = await GetAsync(client, "/en/about");
        using HttpResponseMessage disposable1 = first;
        using HttpResponseMessage disposable2 = second;

        Assert.Contains(ScriptTag, firstBody, StringComparison.Ordinal);
        Assert.False(first.Headers.Contains("Set-Cookie"));
        Assert.False(second.Headers.Contains("Set-Cookie"));
        Assert.True(second.Headers.Contains("Age"), "The second response was not served from the output cache.");
        Assert.Equal(firstBody, secondBody);
    }

    [Theory]
    [InlineData("/en/admin")]
    [InlineData("/ru/signin")]
    [InlineData("/en/dotnet/profile")]
    public async Task AdminSignInAndProfile_HaveNoScript(string path)
    {
        await using TrainerWebFactory factory = await CreateAsync(WebsiteId);
        using HttpClient client = factory.CreateNonRedirectingClient();

        (HttpResponseMessage response, string body) = await GetAsync(client, path);
        using HttpResponseMessage disposable = response;

        Assert.DoesNotContain("data-website-id", body, StringComparison.Ordinal);
        Assert.DoesNotContain(ScriptTag, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IslandPage_ScriptHasNoNonceAndCspUnchanged()
    {
        await using TrainerWebFactory factory = await CreateAsync(WebsiteId);
        using HttpClient client = factory.CreateNonRedirectingClient();

        (HttpResponseMessage response, string body) = await GetAsync(client, "/en/dotnet/practice");
        using HttpResponseMessage disposable = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(ScriptTag, body, StringComparison.Ordinal);
        int start = body.IndexOf("<script defer " + ScriptTag, StringComparison.Ordinal);
        string tag = body[start..(body.IndexOf("</script>", start, StringComparison.Ordinal))];
        Assert.DoesNotContain("nonce", tag, StringComparison.Ordinal);

        string csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.DoesNotContain("/stats", csp, StringComparison.Ordinal);
        Assert.Contains("script-src 'self' 'wasm-unsafe-eval'", csp, StringComparison.Ordinal);
        Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidWebsiteId_FailsStartup()
    {
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(
            postgres,
            new TrainerWebFactoryOptions { Settings = new Dictionary<string, string?> { ["App:Analytics:WebsiteId"] = "not-a-guid" } });

        Exception thrown = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("App:Analytics:WebsiteId", thrown.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GoatCounter_RenderedSameOriginOnPublicPagesNotOnProfile()
    {
        await using TrainerWebFactory factory = await StaticCspSiteFixture.CreateFactoryAsync(postgres, StaticCspSiteFixture.StaticSettings());

        foreach (string path in new[] { "/dit/en/dotnet/questions/", "/dit/en/about/", "/dit/ru/dotnet/practice/" })
        {
            (HttpResponseMessage response, _, IHtmlDocument document) = await StaticCspTests.FetchAsync(factory, path);
            using HttpResponseMessage disposable = response;

            IElement counter = Assert.Single(document.QuerySelectorAll("script[data-goatcounter]"));
            Assert.Equal("https://dit-test.goatcounter.com/count", counter.GetAttribute("data-goatcounter"));
            Assert.Matches(new Regex(@"^/dit/js/goatcounter(\.[a-z0-9]+)?\.js$", RegexOptions.CultureInvariant), counter.GetAttribute("src"));
            Assert.False(counter.HasAttribute("integrity"));
            Assert.False(counter.HasAttribute("crossorigin"));
            Assert.True(counter.HasAttribute("async"));

            // Settings first, and both at the end of the body: the counter reads window.goatcounter when it runs.
            IElement settings = Assert.Single(document.QuerySelectorAll("script[src*=\"goatcounter-settings\"]"));
            Assert.True(counter.CompareDocumentPosition(settings).HasFlag(DocumentPositions.Preceding));
            Assert.Same(counter, document.Body!.LastElementChild);
            Assert.Empty(document.QuerySelectorAll("script[data-website-id], script[src*=\"/stats/\"]"));
        }

        (HttpResponseMessage profile, _, IHtmlDocument profileDocument) = await StaticCspTests.FetchAsync(factory, "/dit/en/dotnet/profile/");
        using HttpResponseMessage profileDisposable = profile;
        Assert.Empty(profileDocument.QuerySelectorAll("script[data-goatcounter], script[src*=\"goatcounter-settings\"], script[src*=\"goatcounter.\"]"));
    }

    [Fact]
    public async Task StaticEdition_IgnoresTheUmamiWebsiteId()
    {
        Dictionary<string, string?> settings = StaticCspSiteFixture.StaticSettings();
        settings["App:Analytics:WebsiteId"] = WebsiteId;
        await using TrainerWebFactory factory = await StaticCspSiteFixture.CreateFactoryAsync(postgres, settings);

        (HttpResponseMessage response, string body, _) = await StaticCspTests.FetchAsync(factory, "/dit/en/dotnet/questions/");
        using HttpResponseMessage disposable = response;

        Assert.DoesNotContain("data-website-id", body, StringComparison.Ordinal);
        Assert.DoesNotContain("/stats/", body, StringComparison.Ordinal);
        Assert.Contains("data-goatcounter=", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("UPPER")]
    [InlineData("a")]
    [InlineData("has space")]
    [InlineData("-abc.def")]
    public void GoatCounterCode_Malformed_FailsStartup(string code)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:Analytics:GoatCounterCode"] = code })
            .Build();

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => AnalyticsSetup.Read(configuration));

        Assert.Contains("App:Analytics:GoatCounterCode", thrown.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dit")]
    [InlineData("my-site-2")]
    public void GoatCounterCode_WellFormed_IsAccepted(string code)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:Analytics:GoatCounterCode"] = code })
            .Build();

        Assert.Equal(code, AnalyticsSetup.Read(configuration).GoatCounterCode);
    }

    [Fact]
    public async Task ServerEdition_IgnoresGoatCounterAndKeepsItsHeaders()
    {
        Dictionary<string, string?> settings = new() { ["App:Analytics:GoatCounterCode"] = "dit-test" };
        TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions { Settings = settings });
        await using TrainerWebFactory disposable = factory;
        await factory.WaitUntilReadyAsync();
        using HttpClient client = factory.CreateNonRedirectingClient();

        (HttpResponseMessage response, string body) = await GetAsync(client, "/en/dotnet/questions");
        using HttpResponseMessage response1 = response;

        Assert.DoesNotContain("gc.zgo.at", body, StringComparison.Ordinal);
        Assert.DoesNotContain("goatcounter", body, StringComparison.OrdinalIgnoreCase);
        string csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.DoesNotContain("goatcounter", csp, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("strict-origin-when-cross-origin", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.DoesNotContain("http-equiv", body, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"site-updated\"", body, StringComparison.Ordinal);
    }
}
