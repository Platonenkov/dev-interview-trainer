using System.Net;
using System.Text.RegularExpressions;

namespace Trainer.Web.Tests;

public sealed partial class CatalogSearchTests(WebSiteFixture site) : IClassFixture<WebSiteFixture>
{
    private const string Catalog = "/en/dotnet/questions";

    [GeneratedRegex(@"href=""([^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex Href();

    [GeneratedRegex(@"<form\b[^>]*>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FormTag();

    [GeneratedRegex(@"<input\b[^>]*name=""q""[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex SearchInput();

    [GeneratedRegex(@"<input\b[^>]*type=""hidden""[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex HiddenInput();

    private static async Task<(HttpResponseMessage Response, string Body)> GetAsync(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        return (response, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private async Task<(HttpResponseMessage Response, string Body)> GetAsync(string path)
    {
        using HttpClient client = site.Factory.CreateClient();
        return await GetAsync(client, path);
    }

    // The output cache middleware adds Age only when it serves a stored entry.
    private static bool IsHit(HttpResponseMessage response) => response.Headers.Contains("Age");

    [Fact]
    public async Task CatalogSearch_IsNoindexAndNotCached()
    {
        using HttpClient client = site.Factory.CreateNonRedirectingClient();

        // Warm the shared entry of the plain catalog first: a search request must not be served from it either.
        (HttpResponseMessage warm, _) = await GetAsync(client, Catalog);
        (HttpResponseMessage warmHit, _) = await GetAsync(client, Catalog);
        (HttpResponseMessage first, string body) = await GetAsync(client, Catalog + "?q=lambdas");
        (HttpResponseMessage second, _) = await GetAsync(client, Catalog + "?q=lambdas");
        using HttpResponseMessage _1 = warm;
        using HttpResponseMessage _2 = warmHit;
        using HttpResponseMessage _3 = first;
        using HttpResponseMessage _4 = second;

        Assert.True(IsHit(warmHit));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.False(IsHit(first));
        Assert.False(IsHit(second));
        Assert.Contains("<meta name=\"robots\" content=\"noindex\"", body, StringComparison.Ordinal);
        Assert.Contains("closure-for-loop", body, StringComparison.Ordinal);
        Assert.DoesNotContain("configureawait-false-purpose", body, StringComparison.Ordinal);
        Assert.False(first.Headers.Contains("Set-Cookie"));
        Assert.False(second.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task CatalogWithoutSearch_StillCached()
    {
        using HttpClient client = site.Factory.CreateNonRedirectingClient();

        (HttpResponseMessage first, string plain) = await GetAsync(client, Catalog + "?level=senior");
        (HttpResponseMessage second, _) = await GetAsync(client, Catalog + "?level=senior");
        // An empty or blank term is no search: the same page, the same cache entry, still indexable.
        (HttpResponseMessage blank, string blankBody) = await GetAsync(client, Catalog + "?level=senior&q=%20%20");
        (HttpResponseMessage empty, _) = await GetAsync(client, Catalog + "?level=senior&q=");
        using HttpResponseMessage _1 = first;
        using HttpResponseMessage _2 = second;
        using HttpResponseMessage _3 = blank;
        using HttpResponseMessage _4 = empty;

        Assert.False(IsHit(first));
        Assert.True(IsHit(second));
        Assert.True(IsHit(blank));
        Assert.True(IsHit(empty));
        Assert.Equal(plain, blankBody);
        Assert.DoesNotContain("noindex", plain, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CatalogSearch_RendersOneLabelledGetFormWithFilters()
    {
        (HttpResponseMessage response, string body) = await GetAsync(Catalog + "?level=middle&topic=async&type=open&page=2&q=await");
        using HttpResponseMessage _ = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string form = Assert.Single(FormTag().Matches(body)).Value;
        Assert.Contains("method=\"get\"", form, StringComparison.Ordinal);
        Assert.Contains("action=\"/en/dotnet/questions\"", form, StringComparison.Ordinal);
        Assert.Contains("role=\"search\"", form, StringComparison.Ordinal);
        string input = Assert.Single(SearchInput().Matches(body)).Value;
        Assert.Contains("type=\"search\"", input, StringComparison.Ordinal);
        Assert.Contains("maxlength=\"100\"", input, StringComparison.Ordinal);
        Assert.Contains("value=\"await\"", input, StringComparison.Ordinal);
        Assert.Matches("<label[^>]*for=\"catalog-search\"", body);
        Assert.Contains("id=\"catalog-search\"", input, StringComparison.Ordinal);

        // The current filters travel with a new search; the page does not, a new search starts on page one.
        string[] hidden = [.. HiddenInput().Matches(body).Select(m => m.Value)];
        Assert.Equal(3, hidden.Length);
        Assert.Contains(hidden, h => h.Contains("name=\"level\"", StringComparison.Ordinal) && h.Contains("value=\"middle\"", StringComparison.Ordinal));
        Assert.Contains(hidden, h => h.Contains("name=\"topic\"", StringComparison.Ordinal) && h.Contains("value=\"async\"", StringComparison.Ordinal));
        Assert.Contains(hidden, h => h.Contains("name=\"type\"", StringComparison.Ordinal) && h.Contains("value=\"open\"", StringComparison.Ordinal));
        Assert.DoesNotContain("__RequestVerificationToken", body, StringComparison.Ordinal);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task CatalogSearch_KeepsTheTermInFilterAndLanguageLinks()
    {
        (HttpResponseMessage response, string body) = await GetAsync(Catalog + "?q=lambdas");
        using HttpResponseMessage _ = response;

        Assert.Contains("href=\"/en/dotnet/questions?level=junior&amp;q=lambdas\"", body, StringComparison.Ordinal);
        Assert.Contains("hreflang=\"ru\" href=\"https://trainer.test/ru/dotnet/questions?q=lambdas\"", body, StringComparison.Ordinal);
        Assert.Contains("rel=\"canonical\" href=\"https://trainer.test/en/dotnet/questions?q=lambdas\"", body, StringComparison.Ordinal);
        // "Practise these" draws from the filters, which do not include a search.
        Assert.Contains("href=\"/en/dotnet/practice\"", body, StringComparison.Ordinal);
        Assert.Contains("href=\"/en/dotnet/questions\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CatalogSearch_MatchesTagsAndTheCulturesTitles()
    {
        (HttpResponseMessage tag, string tagBody) = await GetAsync(Catalog + "?q=Synchronization%20Context");
        (HttpResponseMessage ru, string ruBody) = await GetAsync("/ru/dotnet/questions?q=%D0%BB%D1%8F%D0%BC%D0%B1%D0%B4%D0%B0%D0%BC%D0%B8");
        (HttpResponseMessage ruMiss, string ruMissBody) = await GetAsync("/ru/dotnet/questions?q=english");
        using HttpResponseMessage _1 = tag;
        using HttpResponseMessage _2 = ru;
        using HttpResponseMessage _3 = ruMiss;

        Assert.Contains("questions/result-deadlock-ui-thread", tagBody, StringComparison.Ordinal);
        Assert.Contains("questions/en-only-question", tagBody, StringComparison.Ordinal);
        Assert.DoesNotContain("questions/closure-for-loop", tagBody, StringComparison.Ordinal);
        Assert.Contains("questions/closure-for-loop", ruBody, StringComparison.Ordinal);
        Assert.DoesNotContain("questions/result-deadlock-ui-thread", ruBody, StringComparison.Ordinal);
        Assert.Contains("Под эти фильтры не подходит ни один вопрос.", ruMissBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CatalogSearch_WithWildcardsAndHostileInput_IsLiteralAndEscaped()
    {
        (HttpResponseMessage percent, string percentBody) = await GetAsync(Catalog + "?q=%25");
        (HttpResponseMessage nul, _) = await GetAsync(Catalog + "?q=a%00b");
        (HttpResponseMessage hostile, string hostileBody) = await GetAsync(Catalog + "?q=%22%3E%3Cscript%3Ealert(1)%3C%2Fscript%3E");
        (HttpResponseMessage longTerm, string longBody) = await GetAsync(Catalog + "?q=" + new string('x', 500));
        using HttpResponseMessage _1 = percent;
        using HttpResponseMessage _2 = nul;
        using HttpResponseMessage _3 = hostile;
        using HttpResponseMessage _4 = longTerm;

        Assert.Equal(HttpStatusCode.OK, percent.StatusCode);
        Assert.Contains("No questions match these filters.", percentBody, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, nul.StatusCode);
        Assert.Equal(HttpStatusCode.OK, hostile.StatusCode);
        Assert.DoesNotContain("<script>alert(1)", hostileBody, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, longTerm.StatusCode);
        Assert.Contains("value=\"" + new string('x', 100) + "\"", longBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CatalogSearch_OffersAWayBackToTheUnsearchedCatalog()
    {
        (HttpResponseMessage searched, string searchedBody) = await GetAsync(Catalog + "?level=junior&q=lambdas");
        (HttpResponseMessage plain, string plainBody) = await GetAsync(Catalog + "?level=junior");
        using HttpResponseMessage _1 = searched;
        using HttpResponseMessage _2 = plain;

        Assert.Contains("href=\"/en/dotnet/questions?level=junior\"", searchedBody, StringComparison.Ordinal);
        Assert.Contains("Clear search", searchedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Clear search", plainBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/en/dotnet/questions?q=lambdas", "Search questions")]
    [InlineData("/ru/dotnet/questions?q=lambdas", "Искать вопросы")]
    public async Task CatalogSearch_IsLocalised(string path, string label)
    {
        (HttpResponseMessage response, string body) = await GetAsync(path);
        using HttpResponseMessage _ = response;

        Assert.Contains(label, body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/en/dotnet/practice?level=middle&q=zzsearchterm")]
    [InlineData("/en/dotnet/mock?level=middle&q=zzsearchterm")]
    public async Task IslandPages_DropTheSearchTermFromTheirOwnLinks(string path)
    {
        (HttpResponseMessage response, string body) = await GetAsync(path);
        using HttpResponseMessage _ = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The language switch and the sign-in return link name this mode with its filters, and those never include a
        // search. The skip link is excluded: on island pages it deliberately repeats the request's own URL.
        string[] links = [.. Href().Matches(body).Select(m => m.Groups[1].Value).Where(h => !h.EndsWith("#main", StringComparison.Ordinal))];
        Assert.Contains(links, h => h.Contains("level=middle", StringComparison.Ordinal));
        Assert.DoesNotContain(links, h => h.Contains("zzsearchterm", StringComparison.Ordinal));
    }
}
