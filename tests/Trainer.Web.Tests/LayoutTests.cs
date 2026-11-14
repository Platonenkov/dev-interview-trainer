using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Trainer.TestSupport;

namespace Trainer.Web.Tests;

public sealed partial class LayoutTests(WebSiteFixture site) : IClassFixture<WebSiteFixture>
{
    [GeneratedRegex(@"<script>(?<code>.*?)</script>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex InlineScript();

    [GeneratedRegex(@"'sha256-(?<hash>[A-Za-z0-9+/=]+)'", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Source();

    [GeneratedRegex(@"<(?:link rel=""stylesheet"" href|script src)=""(?<url>[^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex AssetUrl();

    private async Task<HttpResponseMessage> GetAsync(string path)
    {
        using HttpClient client = site.Factory.CreateClient();
        return await client.GetAsync(path, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RuHome_HasHtmlLangRu_AndRussianStrings()
    {
        using HttpResponseMessage response = await GetAsync("/ru");
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("<html lang=\"ru\">", body, StringComparison.Ordinal);
        Assert.Contains("Вопросы к собеседованию для .NET-разработчиков", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Interview questions for", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnHome_HasHtmlLangEn_AndEnglishStrings()
    {
        using HttpResponseMessage response = await GetAsync("/en");
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("<html lang=\"en\">", body, StringComparison.Ordinal);
        Assert.Contains("Interview questions for .NET developers", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Resx_RuAndEn_HaveSameKeys()
    {
        string folder = Path.Combine(ContentWorkspace.RepositoryRoot(), "src", "Trainer.Web", "Localization", "Resources");

        string[] ru = KeysOf(Path.Combine(folder, "SharedResource.ru.resx"));
        string[] en = KeysOf(Path.Combine(folder, "SharedResource.en.resx"));

        Assert.NotEmpty(en);
        Assert.Equal(en, ru);
    }

    [Fact]
    public async Task Pages_HaveCspWithThemeScriptHash()
    {
        using HttpResponseMessage response = await GetAsync("/en");
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        string csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Match hash = Sha256Source().Match(csp);
        Assert.True(hash.Success, csp);

        string inline = InlineScript().Match(body).Groups["code"].Value;
        Assert.NotEmpty(inline);
        string expected = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(inline)));
        Assert.Equal(expected, hash.Groups["hash"].Value);

        Assert.StartsWith("default-src 'self'; script-src 'self' 'sha256-", csp, StringComparison.Ordinal);
        Assert.EndsWith(
            "; style-src 'self'; img-src 'self' https: data:; font-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'",
            csp,
            StringComparison.Ordinal);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("strict-origin-when-cross-origin", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
    }

    [Fact]
    public async Task ThemeScript_PrecedesStylesheets()
    {
        using HttpResponseMessage response = await GetAsync("/en");
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        int head = body.IndexOf("<head>", StringComparison.Ordinal);
        int script = body.IndexOf("<script>", StringComparison.Ordinal);
        int stylesheet = body.IndexOf("rel=\"stylesheet\"", StringComparison.Ordinal);

        Assert.True(head >= 0 && script > head, "the inline theme script must be inside <head>");
        Assert.True(stylesheet > script, "the theme script must come before the first stylesheet");
        RuntimeAssertions.AssertNoRuntime(body);
    }

    [Fact]
    public async Task AssetLinks_AreRootRelative_AndResolve()
    {
        using HttpResponseMessage page = await GetAsync("/ru/dotnet");
        string body = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        string[] urls = [.. AssetUrl().Matches(body).Select(m => m.Groups["url"].Value)];

        // tokens.css, site.css, theme-toggle.js and runtime-preload.js.
        Assert.Equal(4, urls.Length);
        foreach (string url in urls)
        {
            Assert.StartsWith("/", url, StringComparison.Ordinal);
            using HttpResponseMessage asset = await GetAsync(url);
            Assert.True(asset.IsSuccessStatusCode, url);
        }
    }

    [Fact]
    public async Task LanguageSwitcher_UsesCanonicalPath_NotTheRawRequest()
    {
        using HttpResponseMessage response = await GetAsync("/ru/dotnet?x=1&utm_source=junk");
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("href=\"/lang/en?return=%2Fen\" rel=\"nofollow\" hreflang=\"en\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("utm_source", body, StringComparison.Ordinal);
        Assert.DoesNotContain("x=1", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LanguageSwitcher_OnCatalog_KeepsOnlyTheCanonicalQuery()
    {
        using HttpResponseMessage response = await GetAsync("/ru/dotnet/questions?level=middle&utm_source=junk");
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("href=\"/lang/en?return=%2Fen%2Fdotnet%2Fquestions%3Flevel%3Dmiddle\" rel=\"nofollow\" hreflang=\"en\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("utm_source", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LanguageSwitcher_KeepsCanonicalHreflangLinksInHead()
    {
        using HttpResponseMessage response = await GetAsync("/ru/dotnet/questions/closure-for-loop");
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("<link rel=\"alternate\" hreflang=\"en\" href=\"https://trainer.test/en/dotnet/questions/closure-for-loop\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<link rel=\"alternate\" hreflang=\"en\" href=\"https://trainer.test/lang/", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LanguageSwitcher_OnSingleLanguageQuestion_DoesNotLinkToTheMissingCulture()
    {
        using HttpResponseMessage response = await GetAsync("/en/dotnet/questions/en-only-question");
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("hreflang=\"ru\" lang=\"ru\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("/ru/dotnet/questions/en-only-question", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LanguageSwitcher_OnNotFound_FallsBackToTheHomePage()
    {
        using HttpResponseMessage response = await GetAsync("/ru/dotnet/questions/no-such-question?x=1");
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("href=\"/lang/en?return=%2Fen\" rel=\"nofollow\" hreflang=\"en\"", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/css/tokens.css")]
    [InlineData("/css/site.css")]
    [InlineData("/js/theme-toggle.js")]
    [InlineData("/fonts/Inter-Regular.woff2")]
    [InlineData("/fonts/JetBrainsMono-Regular.woff2")]
    public async Task StaticAssets_AreServed(string path)
    {
        using HttpResponseMessage response = await GetAsync(path);

        Assert.True(response.IsSuccessStatusCode, path);
    }

    private static string[] KeysOf(string resxPath) =>
        [.. XDocument.Load(resxPath).Root!.Elements("data").Select(e => (string)e.Attribute("name")!).Order(StringComparer.Ordinal)];
}
