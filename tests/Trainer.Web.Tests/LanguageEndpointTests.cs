using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Trainer.Domain.Keys;
using Trainer.Web.Routing;

namespace Trainer.Web.Tests;

public sealed class LanguageEndpointTests(WebSiteFixture site) : IClassFixture<WebSiteFixture>
{
    private static readonly CultureKey En = new("en");

    private async Task<HttpResponseMessage> GetAsync(string path, bool https = false)
    {
        using HttpClient client = site.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri(https ? "https://localhost" : "http://localhost"),
        });
        return await client.GetAsync(path, TestContext.Current.CancellationToken);
    }

    public static TheoryData<string?, string> ReturnPaths() => new()
    {
        { "/en", "/en" },
        { "/en/dotnet/questions?level=middle", "/en/dotnet/questions?level=middle" },
        { "/en/about", "/en/about" },
        { null, "/en" },
        { "", "/en" },
        { "//evil.com", "/en" },
        { "/\\evil.com", "/en" },
        { "\\\\evil.com", "/en" },
        { "https://evil.com", "/en" },
        { "https://evil.com/en/x", "/en" },
        { "javascript:alert(1)", "/en" },
        { "evil.com", "/en" },
        { "/ru/about", "/en" },
        { "/english", "/en" },
        { "/en//evil.com", "/en" },
        { "/en/\\evil.com", "/en" },
        { "/en/a\r\nSet-Cookie: x=1", "/en" },
        { "/en/a\tb", "/en" },
        { "/%2Fevil.com", "/en" },
        { "/en/a?next=//evil.com", "/en" },
    };

    [Theory]
    [MemberData(nameof(ReturnPaths))]
    public void ResolveReturnPath_AcceptsOnlyLocalPathsInsideTheCulture(string? candidate, string expected)
    {
        Assert.Equal(expected, LanguageEndpoint.ResolveReturnPath(candidate, En));
    }

    [Theory]
    [InlineData("//evil.com")]
    [InlineData("/\\evil.com")]
    [InlineData("https://evil")]
    [InlineData("/ru/about")]
    [InlineData("%2F%2Fevil.com")]
    [InlineData("%2F%5Cevil.com")]
    [InlineData("%2Fen%2F%2Fevil.com")]
    [InlineData("%2Fen%0d%0aSet-Cookie%3Ax%3D1")]
    public async Task Endpoint_RejectsOpenRedirectAttempts(string rawReturn)
    {
        using HttpResponseMessage response = await GetAsync($"/lang/en?return={rawReturn}");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/en", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Endpoint_RedirectsToTheValidatedPathAndSetsTheCookie()
    {
        using HttpResponseMessage response = await GetAsync("/lang/ru?return=%2Fru%2Fdotnet%2Fquestions%3Flevel%3Dmiddle");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/ru/dotnet/questions?level=middle", response.Headers.Location?.OriginalString);
        string cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("lang=ru;", cookie, StringComparison.Ordinal);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=31536000", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("%2Fen%2F%D0%B2", "/en/%D0%B2")]
    [InlineData("%2Fen%2Fdotnet%2Fquestions%3Fq%3D%D0%B2%26level%3Dmiddle", "/en/dotnet/questions?q=%D0%B2&level=middle")]
    [InlineData("%2Fen%2F%F0%9F%98%80", "/en/%F0%9F%98%80")]
    [InlineData("%2Fen%2F%ED%A0%80", "/en/%ED%A0%80")]
    public async Task Endpoint_NonAsciiReturn_YieldsAnAsciiPercentEncodedLocation(string rawReturn, string expectedLocation)
    {
        using HttpResponseMessage response = await GetAsync($"/lang/en?return={rawReturn}");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(expectedLocation, response.Headers.Location?.OriginalString);
    }

    [Fact]
    public void ToLocation_KeepsAsciiPathsUntouched()
    {
        Assert.Equal("/en/dotnet/questions?level=middle&topic=a%20b", LanguageEndpoint.ToLocation("/en/dotnet/questions?level=middle&topic=a%20b"));
    }

    [Fact]
    public async Task Endpoint_OverHttps_MarksTheCookieSecure()
    {
        using HttpResponseMessage response = await GetAsync("/lang/en?return=%2Fen", https: true);

        string cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Endpoint_IsNeverCachedAndNotIndexed()
    {
        using HttpResponseMessage response = await GetAsync("/lang/en?return=%2Fen");

        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("noindex", Assert.Single(response.Headers.GetValues("X-Robots-Tag")));
        Assert.False(response.Headers.Contains("Age"));
    }

    [Theory]
    [InlineData("/lang/de?return=%2Fde")]
    [InlineData("/lang/EN?return=%2Fen")]
    [InlineData("/lang/xx")]
    public async Task Endpoint_UnsupportedCulture_Returns404WithoutCookie(string path)
    {
        using HttpResponseMessage response = await GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Root_HonoursTheCookieSetByTheEndpoint()
    {
        using HttpClient client = site.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using (HttpResponseMessage choose = await client.GetAsync("/lang/ru?return=%2Fru", TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Found, choose.StatusCode);
        }

        using HttpRequestMessage root = new(HttpMethod.Get, "/");
        root.Headers.AcceptLanguage.ParseAdd("en");
        using HttpResponseMessage response = await client.SendAsync(root, TestContext.Current.CancellationToken);

        Assert.Equal("/ru", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task PublicPage_StillSetsNoCookie()
    {
        using HttpResponseMessage response = await GetAsync("/ru/dotnet");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public void SwitchUrl_EncodesTheTargetPathAndQuery()
    {
        Assert.Equal(
            "/lang/en?return=%2Fen%2Fdotnet%2Fquestions%3Flevel%3Dmiddle%26topic%3Dasync",
            LanguageEndpoint.SwitchUrl(En, "/en/dotnet/questions?level=middle&topic=async"));
    }
}
