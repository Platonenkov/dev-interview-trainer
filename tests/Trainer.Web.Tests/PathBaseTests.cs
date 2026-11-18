using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Trainer.TestSupport;

namespace Trainer.Web.Tests;

public sealed partial class PathBaseTests(StaticSiteFixture site, PostgresFixture postgres) : IClassFixture<StaticSiteFixture>
{
    private const string Prefix = StaticSiteFixture.PathBase + "/";
    private const string AbsolutePrefix = StaticSiteFixture.Origin + Prefix;

    private static readonly string[] PublicPages =
    [
        "/dit/",
        "/dit/en",
        "/dit/ru",
        "/dit/en/dotnet",
        "/dit/en/dotnet/questions",
        "/dit/en/dotnet/questions?topic=async&level=middle&page=1",
        "/dit/en/dotnet/topics/async",
        "/dit/en/dotnet/questions/closure-for-loop",
        "/dit/ru/dotnet/questions/closure-for-loop",
        "/dit/en/about",
        "/dit/en/signin",
        "/dit/en/dotnet/questions/no-such-question",
    ];

    private static readonly string[] IslandPages =
    [
        "/dit/en/dotnet/practice/",
        "/dit/en/dotnet/practice/?topic=async&level=junior",
        "/dit/ru/dotnet/mock/",
        "/dit/en/dotnet/review/",
        "/dit/ru/dotnet/profile/",
    ];

    // The static edition exempts no URL from the trailing-slash rule: it emits only page directories and files. These
    // endpoints exist in the server edition alone, so a link to one is a bug.
    private static readonly string[] ServerOnlyEndpoints = ["lang/", "signin-telegram/"];

    [GeneratedRegex(@"https://example\.test(?!/dit/)", RegexOptions.CultureInvariant)]
    private static partial Regex OriginWithoutPathBase();

    private static async Task<(HttpResponseMessage Response, string Body)> GetAsync(TrainerWebFactory factory, string path)
    {
        using HttpClient client = factory.CreateNonRedirectingClient();
        HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        return (response, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static List<string> UrlsOf(IHtmlDocument document)
    {
        List<string> urls = [];
        foreach (IElement element in document.All)
        {
            foreach (string attribute in new[] { "href", "src", "action" })
            {
                string? value = element.GetAttribute(attribute);
                if (value is not null)
                {
                    urls.Add(value);
                }
            }

            string? srcset = element.GetAttribute("srcset");
            if (srcset is not null)
            {
                urls.AddRange(srcset.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(candidate => candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]));
            }

            string? preload = element.GetAttribute("data-preload-runtime");
            if (preload is not null)
            {
                urls.AddRange(preload.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }

            if (element is IHtmlMetaElement meta && meta.GetAttribute("property")?.StartsWith("og:", StringComparison.Ordinal) == true
                && meta.Content is { } content)
            {
                urls.Add(content);
            }

            if (element is IHtmlScriptElement { Type: "importmap" } importMap)
            {
                using JsonDocument map = JsonDocument.Parse(importMap.Text);
                urls.AddRange(ImportMapValues(map.RootElement));
            }
        }

        return urls;
    }

    private static IEnumerable<string> ImportMapValues(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => [element.GetString()!],
        JsonValueKind.Object => element.EnumerateObject().SelectMany(property => ImportMapValues(property.Value)),
        _ => [],
    };

    private static string PathOf(string url)
    {
        string withoutOrigin = url.StartsWith(StaticSiteFixture.Origin, StringComparison.Ordinal) ? url[StaticSiteFixture.Origin.Length..] : url;
        int tail = withoutOrigin.IndexOfAny(['?', '#']);
        return tail < 0 ? withoutOrigin : withoutOrigin[..tail];
    }

    private static void AssertCarriesPathBase(string page, IEnumerable<string> urls)
    {
        foreach (string url in urls)
        {
            bool rootRelative = url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal);
            bool own = url.StartsWith(StaticSiteFixture.Origin, StringComparison.Ordinal);
            if (!rootRelative && !own)
            {
                continue;
            }

            Assert.True(
                url.StartsWith(rootRelative ? Prefix : AbsolutePrefix, StringComparison.Ordinal),
                $"{page}: '{url}' does not carry {Prefix}");

            string path = PathOf(url);
            Assert.False(
                ServerOnlyEndpoints.Any(endpoint => path.StartsWith(Prefix + endpoint, StringComparison.Ordinal)),
                $"{page}: the static edition links to the server-only endpoint '{url}'");
            bool looksLikeFile = path[(path.LastIndexOf('/') + 1)..].Contains('.', StringComparison.Ordinal);
            Assert.True(looksLikeFile || path.EndsWith('/'), $"{page}: page URL '{url}' lacks the trailing slash");
        }
    }

    [Fact]
    public async Task EveryRootRelativeUrlCarriesThePathBase()
    {
        HtmlParser parser = new();
        foreach (string page in PublicPages.Concat(IslandPages))
        {
            (HttpResponseMessage response, string body) = await GetAsync(site.Factory, page);
            using HttpResponseMessage _ = response;
            Assert.True(
                response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound,
                $"{page}: unexpected status {(int)response.StatusCode}");

            List<string> urls = UrlsOf(parser.ParseDocument(body));
            Assert.NotEmpty(urls);
            AssertCarriesPathBase(page, urls);
            Assert.DoesNotMatch(OriginWithoutPathBase(), body);
        }
    }

    [Fact]
    public async Task RedirectSource_LocationCarriesThePathBase()
    {
        (HttpResponseMessage response, _) = await GetAsync(site.Factory, "/dit/ru/dotnet/questions/old-closure");
        using HttpResponseMessage _1 = response;

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal("/dit/ru/dotnet/questions/closure-for-loop/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task StaticEdition_MarksTheDocument_AndServerEditionDoesNot()
    {
        (HttpResponseMessage staticResponse, string staticBody) = await GetAsync(site.Factory, "/dit/en");
        using HttpResponseMessage _1 = staticResponse;
        await using TrainerWebFactory serverFactory = await TrainerWebFactory.CreateAsync(postgres);
        await serverFactory.WaitUntilReadyAsync();
        (HttpResponseMessage serverResponse, string serverBody) = await GetAsync(serverFactory, "/en");
        using HttpResponseMessage _2 = serverResponse;

        Assert.Contains("<html lang=\"en\" data-edition=\"static\">", staticBody, StringComparison.Ordinal);
        Assert.Contains("<html lang=\"en\">", serverBody, StringComparison.Ordinal);
        Assert.DoesNotContain("data-edition", serverBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sitemaps_PageLocsSlashedFileLocsNot()
    {
        (HttpResponseMessage index, string indexBody) = await GetAsync(site.Factory, "/dit/sitemap.xml");
        (HttpResponseMessage ru, string ruBody) = await GetAsync(site.Factory, "/dit/sitemap-ru.xml");
        (HttpResponseMessage robots, string robotsBody) = await GetAsync(site.Factory, "/dit/robots.txt");
        using HttpResponseMessage _1 = index;
        using HttpResponseMessage _2 = ru;
        using HttpResponseMessage _3 = robots;

        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        XNamespace xhtml = "http://www.w3.org/1999/xhtml";
        string[] indexLocs = [.. XDocument.Parse(indexBody).Descendants(ns + "loc").Select(l => l.Value)];
        string[] pageLocs = [.. XDocument.Parse(ruBody).Descendants(ns + "loc").Select(l => l.Value)];
        string[] alternates = [.. XDocument.Parse(ruBody).Descendants(xhtml + "link").Select(l => (string)l.Attribute("href")!)];

        Assert.NotEmpty(indexLocs);
        Assert.All(indexLocs, loc => Assert.Matches("^https://example\\.test/dit/sitemap-[a-z]{2}\\.xml$", loc));
        Assert.Contains(AbsolutePrefix + "ru/dotnet/questions/closure-for-loop/", pageLocs);
        Assert.Contains(AbsolutePrefix + "ru/", pageLocs);
        Assert.All(pageLocs, loc => Assert.StartsWith(AbsolutePrefix, loc, StringComparison.Ordinal));
        Assert.All(pageLocs, loc => Assert.EndsWith("/", loc, StringComparison.Ordinal));
        Assert.NotEmpty(alternates);
        Assert.All(alternates, href => Assert.EndsWith("/", href, StringComparison.Ordinal));
        Assert.Contains("Sitemap: " + AbsolutePrefix + "sitemap.xml\n", robotsBody.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublicPages_HaveNoBaseElement()
    {
        HtmlParser parser = new();
        foreach (string page in new[] { "/dit/en", "/dit/en/dotnet/questions", "/dit/en/dotnet/questions/closure-for-loop" })
        {
            (HttpResponseMessage response, string body) = await GetAsync(site.Factory, page);
            using HttpResponseMessage _ = response;

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Null(parser.ParseDocument(body).QuerySelector("base"));
        }
    }

    [Fact]
    public async Task IslandPages_BaseElementPointsAtThePathBase()
    {
        (HttpResponseMessage response, string body) = await GetAsync(site.Factory, "/dit/en/dotnet/practice");
        using HttpResponseMessage _ = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/dit/", new HtmlParser().ParseDocument(body).QuerySelector("base")?.GetAttribute("href"));
    }

    [GeneratedRegex(@"\b(href|src|action)=""([^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex UrlAttribute();

    [GeneratedRegex(@"\.[a-z0-9]{10}\.(js|css)$", RegexOptions.CultureInvariant)]
    private static partial Regex Fingerprint();

    private static readonly string[] SnapshotPages =
    [
        "/en",
        "/ru",
        "/en/dotnet/questions?topic=async&level=middle",
        "/en/dotnet/questions/closure-for-loop",
        "/en/dotnet/topics/async",
        "/en/about",
        "/en/signin",
        "/en/dotnet/practice",
        "/ru/dotnet/mock",
    ];

    // Recorded from the build before URLs went through SitePaths (commit fc399a5), with asset fingerprints masked.
    [Fact]
    public async Task ServerEdition_WithoutPath_KeepsTodaysUrls()
    {
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres);
        await factory.WaitUntilReadyAsync();

        StringBuilder actual = new();
        foreach (string page in SnapshotPages)
        {
            (HttpResponseMessage response, string body) = await GetAsync(factory, page);
            using HttpResponseMessage _ = response;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            actual.Append("== ").Append(page).Append('\n');
            foreach (Match match in UrlAttribute().Matches(body))
            {
                actual.Append(match.Groups[1].Value).Append('=').Append(Fingerprint().Replace(match.Groups[2].Value, ".{fp}.$1")).Append('\n');
            }
        }

        string expected = File.ReadAllText(Path.Combine(ContentWorkspace.RepositoryRoot(), "tests", "Trainer.Web.Tests", "Snapshots", "server-urls.txt"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Equal(expected, actual.ToString());
    }

    [Fact]
    public async Task ServerEdition_WithoutPath_RobotsKeepsTodaysLines()
    {
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres);
        await factory.WaitUntilReadyAsync();

        (HttpResponseMessage response, string body) = await GetAsync(factory, "/robots.txt");
        using HttpResponseMessage _ = response;

        string[] lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        Assert.Contains("Disallow: /api/", lines);
        Assert.Contains("Disallow: /lang/", lines);
        Assert.Contains("Disallow: /ru/*/practice", lines);
        Assert.Contains("Sitemap: https://trainer.test/sitemap.xml", lines);
    }

    [Fact]
    public async Task Robots_DisallowLinesCarryThePathBase()
    {
        (HttpResponseMessage response, string body) = await GetAsync(site.Factory, "/dit/robots.txt");
        using HttpResponseMessage _ = response;

        string[] disallows = [.. body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Where(l => l.StartsWith("Disallow:", StringComparison.Ordinal))];
        Assert.Contains("Disallow: /dit/api/", disallows);
        Assert.Contains("Disallow: /dit/lang/", disallows);
        Assert.Contains("Disallow: /dit/signin-telegram/", disallows);
        Assert.Contains("Disallow: /dit/en/signin", disallows);
        Assert.Contains("Disallow: /dit/ru/*/practice", disallows);
        Assert.All(disallows, line => Assert.StartsWith("Disallow: /dit/", line, StringComparison.Ordinal));
    }
}
