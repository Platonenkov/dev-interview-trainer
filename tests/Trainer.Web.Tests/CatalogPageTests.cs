using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Trainer.Web.Routing;

namespace Trainer.Web.Tests;

public sealed partial class CatalogPageTests(WebSiteFixture site) : IClassFixture<WebSiteFixture>
{
    [GeneratedRegex(@"<a[^>]*aria-current=""true""[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex CurrentLink();

    private async Task<(HttpResponseMessage Response, string Body)> GetAsync(string path)
    {
        using HttpClient client = site.Factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        return (response, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Catalog_RendersRowsWithLinksToQuestionPages()
    {
        (HttpResponseMessage response, string body) = await GetAsync("/ru/dotnet/questions");
        using HttpResponseMessage _ = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("href=\"/ru/dotnet/questions/closure-for-loop\"", body, StringComparison.Ordinal);
        Assert.Contains("Что выведет код с лямбдами", body, StringComparison.Ordinal);
        // The English-only question has no Russian locale and must not be linked from the Russian catalog.
        Assert.DoesNotContain("/questions/en-only-question", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Catalog_InEnglish_ListsTheEnglishOnlyQuestion()
    {
        (HttpResponseMessage response, string body) = await GetAsync("/en/dotnet/questions");
        using HttpResponseMessage _ = response;

        Assert.Contains("href=\"/en/dotnet/questions/en-only-question\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Catalog_JunkQuery_Returns200AndMarksOnlyTheValidFilterSelected()
    {
        (HttpResponseMessage response, string body) = await GetAsync("/en/dotnet/questions?level=MIDDLE&level=middle&topic=nope&utm_source=x");
        using HttpResponseMessage _ = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string current = Assert.Single(CurrentLink().Matches(body)).Value;
        Assert.Contains("href=\"/en/dotnet/questions\"", current, StringComparison.Ordinal);
        Assert.Contains("(selected)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("closure-for-loop", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Catalog_FilterLinksToggleOneValueInTheCanonicalQuery()
    {
        (HttpResponseMessage response, string body) = await GetAsync("/en/dotnet/questions?level=middle");
        using HttpResponseMessage _ = response;

        Assert.Contains("href=\"/en/dotnet/questions?level=junior&amp;level=middle\"", body, StringComparison.Ordinal);
        Assert.Contains("href=\"/en/dotnet/questions?level=middle&amp;topic=async\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Catalog_EmptySelection_ShowsMessage()
    {
        (HttpResponseMessage response, string body) = await GetAsync("/ru/dotnet/questions?level=architect");
        using HttpResponseMessage _ = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Под эти фильтры не подходит ни один вопрос.", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/ru", 0)]
    [InlineData("/en/dotnet", 0)]
    [InlineData("/ru/about", 0)]
    [InlineData("/en/about", 0)]
    [InlineData("/ru/dotnet/questions", 1)]
    [InlineData("/en/dotnet/questions?level=middle&topic=async", 1)]
    [InlineData("/en/dotnet/questions/closure-for-loop", 0)]
    public async Task PublicPages_HaveOnlyTheGetSearchFormAndNoSetCookie(string path, int searchForms)
    {
        (HttpResponseMessage response, string body) = await GetAsync(path);
        using HttpResponseMessage _ = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        MatchCollection forms = Regex.Matches(body, @"<form[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        Assert.Equal(searchForms, forms.Count);
        Assert.All(forms, form => Assert.Contains("method=\"get\"", form.Value, StringComparison.Ordinal));
        Assert.DoesNotContain("__RequestVerificationToken", body, StringComparison.Ordinal);
        RuntimeAssertions.AssertNoRuntime(body);
        Assert.False(response.Headers.Contains("Set-Cookie"), path);
    }

    [Fact]
    public async Task About_IsLocalised()
    {
        (HttpResponseMessage ru, string ruBody) = await GetAsync("/ru/about");
        (HttpResponseMessage en, string enBody) = await GetAsync("/en/about");
        using HttpResponseMessage _1 = ru;
        using HttpResponseMessage _2 = en;

        Assert.Contains("<html lang=\"ru\">", ruBody, StringComparison.Ordinal);
        Assert.Contains("Лицензии", ruBody, StringComparison.Ordinal);
        Assert.Contains("CC BY-SA 4.0", enBody, StringComparison.Ordinal);
        Assert.Contains("How to contribute", enBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/en/about", "sets no tracking or analytics cookies", "sign-in cookie")]
    [InlineData("/ru/about", "не устанавливает читателям cookie для отслеживания или аналитики", "cookie входа")]
    public async Task About_PrivacyStatement_NamesTheOnlyReaderCookieTheCodeSets(string path, string noTrackingClaim, string signInClaim)
    {
        (HttpResponseMessage page, string body) = await GetAsync(path);
        using HttpResponseMessage _1 = page;
        using HttpClient client = site.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        using HttpResponseMessage switched = await client.GetAsync("/lang/en?return=%2Fen", TestContext.Current.CancellationToken);

        Assert.Contains(noTrackingClaim, body, StringComparison.Ordinal);
        Assert.Contains(signInClaim, body, StringComparison.Ordinal);
        Assert.Contains($"{RootRedirectEndpoint.CookieName} ", body, StringComparison.Ordinal);
        string cookie = Assert.Single(switched.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith(RootRedirectEndpoint.CookieName + "=", cookie, StringComparison.Ordinal);
    }
}
