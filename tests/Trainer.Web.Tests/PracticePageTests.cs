using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Trainer.Contracts;
using Trainer.TestSupport;

namespace Trainer.Web.Tests;

/// <summary>
/// The Practice page shell and the public entry points into it: links into island pages are full page loads and carry
/// the runtime preload URLs, while public pages themselves never load the runtime (spec 8.2, ADR 0009).
/// </summary>
public sealed partial class PracticePageTests(WebSiteFixture site) : IClassFixture<WebSiteFixture>
{
    private const string ClosureId = "01KBZ3Q8N9V2K3M4P5R6S7T8WA";

    private static readonly string[] PublicPages =
    [
        "/en",
        "/ru/dotnet",
        "/en/dotnet/questions?level=middle&topic=async",
        "/en/dotnet/topics/async",
        "/en/dotnet/questions/closure-for-loop",
        "/en/about",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("<!--Blazor:(?<marker>\\{.*?\\})-->", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex ComponentMarker();

    [GeneratedRegex("<a\\s[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex Anchor();

    [GeneratedRegex("href=\"(?<href>[^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex Href();

    private async Task<string> GetBodyAsync(string path)
    {
        using HttpClient client = site.Factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    private static IslandBootstrapDto Bootstrap(string body)
    {
        Match match = ComponentMarker().Match(body);
        Assert.True(match.Success, "No WebAssembly component marker.");
        using JsonDocument marker = JsonDocument.Parse(match.Groups["marker"].Value);
        string values = Encoding.UTF8.GetString(Convert.FromBase64String(marker.RootElement.GetProperty("parameterValues").GetString()!));
        using JsonDocument parameters = JsonDocument.Parse(values);
        string json = parameters.RootElement[0].GetString()!;
        return JsonSerializer.Deserialize(json, TrainerJsonContext.Default.IslandBootstrapDto)!;
    }

    private static IEnumerable<string> Anchors(string body) => Anchor().Matches(body).Select(m => m.Value);

    private static string HrefOf(string anchor) => WebUtility.HtmlDecode(Href().Match(anchor).Groups["href"].Value);

    private static bool IsIslandLink(string anchor)
    {
        string href = HrefOf(anchor);
        string path = href.Split('?', '#')[0];
        return Regex.IsMatch(path, "^/[a-z]{2}/[a-z0-9-]+/(practice|mock|review|profile)$", RegexOptions.CultureInvariant);
    }

    [Fact]
    public async Task PracticePage_ShellHasSkeletonIslandMarkerAndBootstrap()
    {
        string body = await GetBodyAsync("/en/dotnet/practice");

        Assert.Contains("<div class=\"island\" data-island=\"practice\">", body, StringComparison.Ordinal);
        Assert.Contains("<div class=\"island-skeleton\" aria-hidden=\"true\"></div>", body, StringComparison.Ordinal);
        Assert.Contains("\"typeName\":\"Trainer.Web.Client.Islands.PracticeIsland\"", body, StringComparison.Ordinal);
        IslandBootstrapDto bootstrap = Bootstrap(body);
        Assert.Equal("en", bootstrap.Culture);
        Assert.Equal("dotnet", bootstrap.Stack);
        Assert.Null(bootstrap.Me);
        Assert.Empty(bootstrap.InitialFilters.Levels);
        Assert.Empty(bootstrap.InitialFilters.Topics);
        Assert.Empty(bootstrap.InitialFilters.Types);
        Assert.Null(bootstrap.InitialFilters.Status);
        Assert.Null(bootstrap.InitialFilters.FirstQuestionId);
    }

    [Fact]
    public async Task PracticePage_FiltersFromQueryNormalised()
    {
        string body = await GetBodyAsync(
            $"/en/dotnet/practice?topic=ASYNC&level=senior&level=junior&level=junior&type=single&type=bogus&status=new&first={ClosureId}&utm_source=x");
        string unknown = await GetBodyAsync("/en/dotnet/practice?status=bogus&first=not-an-id");

        PracticeFiltersDto filters = Bootstrap(body).InitialFilters;
        Assert.Equal(["junior", "senior"], filters.Levels);
        Assert.Equal(["async"], filters.Topics);
        Assert.Equal(["single"], filters.Types);
        Assert.Equal("new", filters.Status);
        Assert.Equal(ClosureId, filters.FirstQuestionId);
        Assert.Contains(
            $"<link rel=\"canonical\" href=\"https://trainer.test/en/dotnet/practice?level=junior&amp;level=senior&amp;topic=async&amp;type=single&amp;status=new&amp;first={ClosureId}\"",
            body,
            StringComparison.Ordinal);

        PracticeFiltersDto fallback = Bootstrap(unknown).InitialFilters;
        Assert.Null(fallback.Status);
        Assert.Null(fallback.FirstQuestionId);
    }

    [Fact]
    public async Task Home_ModeCardsLinkToModesWithoutEnhancedNav()
    {
        string body = await GetBodyAsync("/en");

        Assert.DoesNotContain("mode-card__soon", body, StringComparison.Ordinal);
        string[] cards = [.. Anchors(body).Where(a => a.Contains("class=\"mode-card\"", StringComparison.Ordinal))];
        Assert.Equal(["/en/dotnet/practice", "/en/dotnet/mock", "/en/dotnet/review"], cards.Select(HrefOf));
        Assert.All(cards, card => Assert.Contains("data-enhance-nav=\"false\"", card, StringComparison.Ordinal));
        Assert.All(cards.Take(2), card => Assert.Contains("data-preload-runtime=\"", card, StringComparison.Ordinal));
        // Review needs an account; an anonymous visitor is told so on the card instead of being sent there blind.
        Assert.Contains("<span class=\"mode-card__action\">Sign in to review</span>", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuestionPage_HasPractiseThisQuestionLink()
    {
        string body = await GetBodyAsync("/en/dotnet/questions/closure-for-loop");
        string ru = await GetBodyAsync("/ru/dotnet/questions/closure-for-loop");

        string link = Assert.Single(Anchors(body), a => HrefOf(a) == $"/en/dotnet/practice?first={ClosureId}");
        Assert.Contains("data-enhance-nav=\"false\"", link, StringComparison.Ordinal);
        Assert.Contains($">Practise this question</a>", body, StringComparison.Ordinal);
        Assert.Contains($"href=\"/ru/dotnet/practice?first={ClosureId}\"", ru, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CatalogAndTopic_PractiseTheseQuestionsCarriesTheFilters()
    {
        string catalog = await GetBodyAsync("/en/dotnet/questions?topic=async&level=middle&type=open&page=2");
        string topic = await GetBodyAsync("/en/dotnet/topics/async");

        Assert.Single(Anchors(catalog), a => HrefOf(a) == "/en/dotnet/practice?level=middle&topic=async&type=open");
        Assert.Single(Anchors(topic), a => HrefOf(a) == "/en/dotnet/practice?topic=async");
        Assert.Contains(">Practise these questions</a>", topic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublicPages_PracticeLinksCarryPreloadAttribute_AndNoBlazorScript()
    {
        foreach (string path in PublicPages.Where(p => !p.EndsWith("/about", StringComparison.Ordinal)))
        {
            string body = await GetBodyAsync(path);

            string[] practice = [.. Anchors(body).Where(a => HrefOf(a).Split('?')[0].EndsWith("/practice", StringComparison.Ordinal))];
            Assert.True(practice.Length > 0, $"{path} has no Practice link.");
            foreach (string link in practice)
            {
                Match preload = Regex.Match(link, "data-preload-runtime=\"(?<urls>[^\"]+)\"", RegexOptions.CultureInvariant);
                Assert.True(preload.Success, $"{path}: {link} has no data-preload-runtime.");
                string[] urls = preload.Groups["urls"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal(2, urls.Length);
                Assert.Matches("^/_framework/blazor\\.web(\\.[a-z0-9]+)?\\.js$", urls[0]);
                Assert.Matches("^/_framework/dotnet(\\.[a-z0-9]+)?\\.js$", urls[1]);
            }

            Assert.Matches("<script src=\"/js/runtime-preload(\\.[a-z0-9]+)?\\.js\" defer></script>", body);
            RuntimeAssertions.AssertNoRuntime(body);
            Assert.DoesNotContain("islands-start", body, StringComparison.Ordinal);
            Assert.DoesNotContain("<link rel=\"modulepreload\"", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task IslandPage_DoesNotLoadRuntimePreload()
    {
        string body = await GetBodyAsync("/en/dotnet/practice");

        // The import map names every script asset, runtime-preload.js included; only a script element would load it.
        Assert.DoesNotMatch("<script[^>]*runtime-preload", body);
        Assert.Contains("<script type=\"importmap\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublicPages_IslandLinksDisableEnhancedNav()
    {
        int checkedLinks = 0;
        foreach (string path in PublicPages)
        {
            string body = await GetBodyAsync(path);
            foreach (string link in Anchors(body).Where(IsIslandLink))
            {
                Assert.True(link.Contains("data-enhance-nav=\"false\"", StringComparison.Ordinal), $"{path}: {link} allows enhanced navigation.");
                checkedLinks++;
            }
        }

        Assert.True(checkedLinks >= 6, $"Only {checkedLinks} island links were found on the public pages.");
    }
}
