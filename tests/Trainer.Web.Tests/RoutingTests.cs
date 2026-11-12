using System.Net;
using Trainer.Web.Routing;

namespace Trainer.Web.Tests;

public sealed class RoutingTests(WebSiteFixture site) : IClassFixture<WebSiteFixture>
{
    private async Task<HttpResponseMessage> GetRootAsync(string? acceptLanguage, string? cookie = null)
    {
        using HttpClient client = site.Factory.CreateNonRedirectingClient();
        using HttpRequestMessage request = new(HttpMethod.Get, "/");
        if (acceptLanguage is not null)
        {
            request.Headers.AcceptLanguage.ParseAdd(acceptLanguage);
        }

        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Root_WithRussianAcceptLanguage_RedirectsToRu()
    {
        using HttpResponseMessage response = await GetRootAsync("de;q=0.5, ru-RU,ru;q=0.9, en;q=0.4");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/ru", response.Headers.Location?.OriginalString);
        Assert.Contains("Accept-Language", response.Headers.Vary);
        Assert.Contains("Cookie", response.Headers.Vary);
    }

    [Fact]
    public async Task Root_WithUnknownLanguage_RedirectsToEn()
    {
        using HttpResponseMessage response = await GetRootAsync("de-DE,fr;q=0.8");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/en", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Root_CookieWinsOverAcceptLanguage()
    {
        using HttpResponseMessage response = await GetRootAsync("ru", cookie: "lang=en");

        Assert.Equal("/en", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Root_WithInvalidCookie_FallsBackToAcceptLanguage()
    {
        using HttpResponseMessage response = await GetRootAsync("ru", cookie: "lang=de");

        Assert.Equal("/ru", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task NotFoundPage_IsNoindexAndNeverCached()
    {
        using HttpClient client = site.Factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/de", TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("<meta name=\"robots\" content=\"noindex\"", body, StringComparison.Ordinal);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task UnsupportedCulture_Returns404WithNotFoundBody() => await AssertNotFoundAsync("/de");

    [Fact]
    public async Task UnknownStack_Returns404WithNotFoundBody() => await AssertNotFoundAsync("/ru/java");

    [Fact]
    public async Task UnmatchedRoute_Returns404WithNotFoundBody() => await AssertNotFoundAsync("/ru/dotnet/no/such/page/at/all");

    [Fact]
    public async Task NotFoundPage_UsesCultureOfTheOriginalPath()
    {
        using HttpClient client = site.Factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/ru/java", TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("<html lang=\"ru\">", body, StringComparison.Ordinal);
        Assert.Contains("Страница не найдена", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/en")]
    [InlineData("/ru")]
    [InlineData("/ru/dotnet")]
    public async Task KnownCultureAndStack_Return200(string path)
    {
        using HttpClient client = site.Factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AnonymousPage_SetsNoCookie()
    {
        using HttpClient client = site.Factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/en", TestContext.Current.CancellationToken);

        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData("/ru/dotnet/csharp?x=1", "en", "/en/dotnet/csharp?x=1")]
    [InlineData("/en", "ru", "/ru")]
    [InlineData("/not-found", "ru", "/ru")]
    [InlineData("/de/dotnet", "en", "/en")]
    public void SwitchCulture_KeepsPathAndQuery_OrFallsBackToHome(string current, string target, string expected)
    {
        Trainer.Domain.Keys.CultureKey.TryCreate(target, out Trainer.Domain.Keys.CultureKey culture);

        Assert.Equal(expected, CulturePath.SwitchCulture(current, culture));
    }

    private async Task AssertNotFoundAsync(string path)
    {
        using HttpClient client = site.Factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("id=\"not-found\"", body, StringComparison.Ordinal);
    }
}
