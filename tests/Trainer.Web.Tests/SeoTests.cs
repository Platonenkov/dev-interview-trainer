using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Trainer.Application.Content;
using Trainer.TestSupport;

namespace Trainer.Web.Tests;

public sealed partial class SeoTests(WebSiteFixture site, PostgresFixture postgres) : IClassFixture<WebSiteFixture>
{
    private const string Origin = "https://trainer.test";

    [GeneratedRegex(@"<link rel=""canonical"" href=""([^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex Canonical();

    [GeneratedRegex(@"<link rel=""alternate"" hreflang=""([^""]*)"" href=""([^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex Alternates();

    [GeneratedRegex(@"<meta name=""robots"" content=""noindex""", RegexOptions.CultureInvariant)]
    private static partial Regex NoIndexMeta();

    [GeneratedRegex(@"<script type=""application/ld\+json"">(.*?)</script>", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex JsonLd();

    [GeneratedRegex(@"<title>", RegexOptions.CultureInvariant)]
    private static partial Regex TitleTag();

    private async Task<(HttpResponseMessage Response, string Body)> GetAsync(string path, TrainerWebFactory? factory = null)
    {
        using HttpClient client = (factory ?? site.Factory).CreateNonRedirectingClient();
        HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        return (response, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private async Task<TrainerWebFactory> CreateFactoryWithoutRetiredQuestionAsync()
    {
        TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres);
        await factory.WaitUntilReadyAsync();
        factory.Content.Delete("dotnet/async/to-be-retired.yaml");
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        IContentImportService import = scope.ServiceProvider.GetRequiredService<IContentImportService>();
        ContentImportResult result = await import.ImportAsync(factory.Content.Root, TestContext.Current.CancellationToken);
        Assert.Equal(ContentImportStatus.Imported, result.Status);
        return factory;
    }

    private static Dictionary<string, string> HreflangMap(string body) =>
        Alternates().Matches(body).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);

    [GeneratedRegex(@"<meta (?:property|name)=""(?<key>og:image(?::[a-z]+)?|twitter:card)"" content=""(?<value>[^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex ImageMeta();

    private static Dictionary<string, string> ImageTags(string body) =>
        ImageMeta().Matches(body).ToDictionary(m => m.Groups["key"].Value, m => m.Groups["value"].Value, StringComparer.Ordinal);

    [Fact]
    public async Task QuestionPage_HasAbsoluteOgImageWithDimensionsAndTwitterCard()
    {
        (HttpResponseMessage response, string body) = await GetAsync("/ru/dotnet/questions/closure-for-loop");
        using HttpResponseMessage _ = response;

        Dictionary<string, string> tags = ImageTags(body);
        Assert.Matches(@"^https://trainer\.test/og/ru/dotnet/closure-for-loop/[0-9a-f]{16}\.png$", tags["og:image"]);
        Assert.Equal("1200", tags["og:image:width"]);
        Assert.Equal("630", tags["og:image:height"]);
        Assert.Contains("лямбдами", tags["og:image:alt"], StringComparison.Ordinal);
        Assert.Equal("summary_large_image", tags["twitter:card"]);
    }

    [Theory]
    [InlineData("/ru", "ru")]
    [InlineData("/en/dotnet/topics/async", "en")]
    [InlineData("/en/about", "en")]
    [InlineData("/ru/dotnet/questions", "ru")]
    public async Task PublicPages_AdvertiseTheSiteCardOfTheirCulture(string path, string culture)
    {
        (HttpResponseMessage response, string body) = await GetAsync(path);
        using HttpResponseMessage _ = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Dictionary<string, string> tags = ImageTags(body);
        Assert.Matches($@"^https://trainer\.test/og/{culture}/site/[0-9a-f]{{16}}\.png$", tags["og:image"]);
        Assert.Equal("summary_large_image", tags["twitter:card"]);
    }

    [Fact]
    public async Task QuestionPage_HasAbsoluteCanonicalAndHreflangPair()
    {
        (HttpResponseMessage response, string body) = await GetAsync("/ru/dotnet/questions/closure-for-loop");
        using HttpResponseMessage _ = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Origin + "/ru/dotnet/questions/closure-for-loop", Assert.Single(Canonical().Matches(body)).Groups[1].Value);
        Dictionary<string, string> alternates = HreflangMap(body);
        Assert.Equal(3, alternates.Count);
        Assert.Equal(Origin + "/ru/dotnet/questions/closure-for-loop", alternates["ru"]);
        Assert.Equal(Origin + "/en/dotnet/questions/closure-for-loop", alternates["en"]);
        Assert.Equal(alternates["en"], alternates["x-default"]);
        Assert.Single(TitleTag().Matches(body));
        Assert.Contains("<meta name=\"description\"", body, StringComparison.Ordinal);
        Assert.Contains("<meta property=\"og:title\"", body, StringComparison.Ordinal);
        Assert.Empty(NoIndexMeta().Matches(body));
    }

    [Fact]
    public async Task Hreflang_ListsOnlyExistingCultures()
    {
        (HttpResponseMessage response, string body) = await GetAsync("/en/dotnet/questions/en-only-question");
        using HttpResponseMessage _ = response;

        Dictionary<string, string> alternates = HreflangMap(body);
        Assert.Equal(["en", "x-default"], alternates.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(Origin + "/en/dotnet/questions/en-only-question", alternates["en"]);
        Assert.Equal(alternates["en"], alternates["x-default"]);
    }

    [Fact]
    public async Task RetiredQuestion_IsNoindex()
    {
        await using TrainerWebFactory factory = await CreateFactoryWithoutRetiredQuestionAsync();

        (HttpResponseMessage response, string body) = await GetAsync("/en/dotnet/questions/to-be-retired", factory);
        using HttpResponseMessage _ = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(NoIndexMeta().Matches(body));
    }

    [Fact]
    public async Task NotFoundPage_IsNoindex()
    {
        (HttpResponseMessage response, string body) = await GetAsync("/en/dotnet/questions/no-such-question");
        using HttpResponseMessage _ = response;

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(NoIndexMeta().Matches(body));
        Assert.Empty(Canonical().Matches(body));
    }

    [Theory]
    [InlineData("/en/dotnet/questions?level=junior&level=middle")]
    [InlineData("/en/dotnet/questions?topic=async&topic=concurrency")]
    [InlineData("/en/dotnet/questions?type=open")]
    public async Task Catalog_MultiLevelFilter_IsNoindex(string path)
    {
        (HttpResponseMessage response, string body) = await GetAsync(path);
        using HttpResponseMessage _ = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(NoIndexMeta().Matches(body));
    }

    [Fact]
    public async Task Catalog_SingleLevel_IsIndexable()
    {
        (HttpResponseMessage response, string body) = await GetAsync("/en/dotnet/questions?topic=async&level=middle&utm_source=x");
        using HttpResponseMessage _ = response;

        Assert.Empty(NoIndexMeta().Matches(body));
        Assert.Equal(
            Origin + "/en/dotnet/questions?level=middle&amp;topic=async",
            Assert.Single(Canonical().Matches(body)).Groups[1].Value);
        Assert.Equal(Origin + "/ru/dotnet/questions?level=middle&amp;topic=async", HreflangMap(body)["ru"]);
    }

    [Fact]
    public async Task Catalog_PastTheLastPage_CanonicalUsesTheClampedPage()
    {
        (HttpResponseMessage response, string body) = await GetAsync("/en/dotnet/questions?page=7");
        using HttpResponseMessage _ = response;

        Assert.Equal(Origin + "/en/dotnet/questions", Assert.Single(Canonical().Matches(body)).Groups[1].Value);
        Assert.Empty(NoIndexMeta().Matches(body));
    }

    [Fact]
    public async Task StackHome_OfDefaultStack_CanonicalisesToCultureHome()
    {
        (HttpResponseMessage response, string body) = await GetAsync("/en/dotnet");
        (HttpResponseMessage bare, string bareBody) = await GetAsync("/en");
        using HttpResponseMessage _1 = response;
        using HttpResponseMessage _2 = bare;

        Assert.Equal(Origin + "/en", Assert.Single(Canonical().Matches(body)).Groups[1].Value);
        Assert.Equal(Origin + "/en", Assert.Single(Canonical().Matches(bareBody)).Groups[1].Value);
        Assert.Equal(Origin + "/ru", HreflangMap(bareBody)["ru"]);
    }

    [Fact]
    public async Task JsonLd_IsValidJsonAndEscapesScript()
    {
        const string title = "Does </script> in a title break the page?";
        (HttpResponseMessage response, string body) = await GetAsync("/en/dotnet/questions/script-in-title");
        using HttpResponseMessage _ = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string json = Assert.Single(JsonLd().Matches(body)).Groups[1].Value;
        Assert.DoesNotContain('<', json);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement items = document.RootElement.GetProperty("itemListElement");
        Assert.Equal("BreadcrumbList", document.RootElement.GetProperty("@type").GetString());
        Assert.Equal(title, items[items.GetArrayLength() - 1].GetProperty("name").GetString());
        Assert.DoesNotContain(title, body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/en")]
    [InlineData("/ru/about")]
    [InlineData("/en/dotnet/questions")]
    [InlineData("/en/dotnet/topics/async")]
    public async Task PublicPages_HaveBreadcrumbListAndCanonical(string path)
    {
        (HttpResponseMessage response, string body) = await GetAsync(path);
        using HttpResponseMessage _ = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(Assert.Single(JsonLd().Matches(body)).Groups[1].Value);
        Assert.Equal("BreadcrumbList", document.RootElement.GetProperty("@type").GetString());
        Assert.StartsWith(Origin, Assert.Single(Canonical().Matches(body)).Groups[1].Value, StringComparison.Ordinal);
        Assert.Single(TitleTag().Matches(body));
    }

    [Fact]
    public async Task Sitemap_ListsPublishedQuestionsPerCulture_ExcludesRetiredAndMissingLocale()
    {
        await using TrainerWebFactory factory = await CreateFactoryWithoutRetiredQuestionAsync();

        (HttpResponseMessage index, string indexBody) = await GetAsync("/sitemap.xml", factory);
        (HttpResponseMessage ru, string ruBody) = await GetAsync("/sitemap-ru.xml", factory);
        (HttpResponseMessage en, string enBody) = await GetAsync("/sitemap-en.xml", factory);
        using HttpResponseMessage _1 = index;
        using HttpResponseMessage _2 = ru;
        using HttpResponseMessage _3 = en;

        Assert.Equal("application/xml; charset=utf-8", index.Content.Headers.ContentType?.ToString());
        Assert.Contains(Origin + "/sitemap-ru.xml", indexBody, StringComparison.Ordinal);
        Assert.Contains(Origin + "/sitemap-en.xml", indexBody, StringComparison.Ordinal);

        HashSet<string> ruUrls = Locations(ruBody);
        HashSet<string> enUrls = Locations(enBody);
        Assert.Contains(Origin + "/ru/dotnet/questions/closure-for-loop", ruUrls);
        Assert.Contains(Origin + "/en/dotnet/questions/closure-for-loop", enUrls);
        Assert.Contains(Origin + "/en/dotnet/questions/en-only-question", enUrls);
        Assert.DoesNotContain(Origin + "/ru/dotnet/questions/en-only-question", ruUrls);
        Assert.DoesNotContain(ruUrls.Concat(enUrls), url => url.Contains("to-be-retired", StringComparison.Ordinal));
        Assert.Contains(Origin + "/ru", ruUrls);
        Assert.Contains(Origin + "/ru/about", ruUrls);
        Assert.Contains(Origin + "/ru/dotnet/questions", ruUrls);
        Assert.Contains(Origin + "/ru/dotnet/topics/async", ruUrls);
        Assert.DoesNotContain(Origin + "/ru/dotnet", ruUrls);
        Assert.DoesNotContain(ruUrls.Concat(enUrls), url => url.EndsWith("/topics/generics", StringComparison.Ordinal));


        XNamespace sitemapNs = "http://www.sitemaps.org/schemas/sitemap/0.9";
        XNamespace xhtml = "http://www.w3.org/1999/xhtml";
        XElement enOnly = XDocument.Parse(enBody).Descendants(sitemapNs + "url")
            .Single(u => u.Element(sitemapNs + "loc")!.Value.EndsWith("/en-only-question", StringComparison.Ordinal));
        Assert.Equal(["en", "x-default"], enOnly.Elements(xhtml + "link").Select(l => (string)l.Attribute("hreflang")!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Robots_ReferencesSitemap()
    {
        (HttpResponseMessage response, string body) = await GetAsync("/robots.txt");
        using HttpResponseMessage _ = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Contains("Sitemap: " + Origin + "/sitemap.xml", body, StringComparison.Ordinal);
        Assert.Contains("Disallow: /api/", body, StringComparison.Ordinal);
        Assert.Contains("Disallow: /lang/", body, StringComparison.Ordinal);
        Assert.Contains("Disallow: /ru/*/practice", body, StringComparison.Ordinal);
        Assert.Contains("Disallow: /en/signin", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublicBaseUrl_MissingInProduction_FailsAtStartup()
    {
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(
            postgres,
            new TrainerWebFactoryOptions
            {
                Environment = "Production",
                Settings = new Dictionary<string, string?> { ["App:PublicBaseUrl"] = "" },
            });

        Exception thrown = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("PublicBaseUrl", thrown.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CanonicalUrls_IgnoreSpoofedHostHeader()
    {
        using HttpClient client = site.Factory.CreateNonRedirectingClient();
        client.DefaultRequestHeaders.Host = "evil.example";
        using HttpResponseMessage response = await client.GetAsync("/en/dotnet/questions/closure-for-loop", TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using HttpResponseMessage robots = await client.GetAsync("/robots.txt", TestContext.Current.CancellationToken);
        string robotsBody = await robots.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.StartsWith(Origin, Assert.Single(Canonical().Matches(body)).Groups[1].Value, StringComparison.Ordinal);
        Assert.All(Alternates().Matches(body), m => Assert.StartsWith(Origin, m.Groups[2].Value, StringComparison.Ordinal));
        Assert.DoesNotContain("evil.example", body, StringComparison.Ordinal);
        Assert.DoesNotContain("evil.example", robotsBody, StringComparison.Ordinal);
    }

    private static HashSet<string> Locations(string xml)
    {
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        return [.. XDocument.Parse(xml).Descendants(ns + "loc").Select(e => e.Value)];
    }
}
