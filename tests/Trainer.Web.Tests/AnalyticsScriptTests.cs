using System.Net;
using Trainer.TestSupport;

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
}
