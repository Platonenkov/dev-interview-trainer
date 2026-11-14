using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Trainer.TestSupport;
using Trainer.Web.Caching;
using Trainer.Web.Security;
using static Trainer.Web.Tests.SignInTestHelpers;

namespace Trainer.Web.Tests;

/// <summary>
/// BREACH: a signed-in HTML page carries an antiforgery token, so it is never compressed and never cached by anyone.
/// Only Razor component pages are touched (and the antiforgery endpoint, for compression); static assets are not.
/// </summary>
public sealed partial class BreachGuardTests(SignInSiteFixture site) : IClassFixture<SignInSiteFixture>
{
    private const string Page = "/en/dotnet/questions";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("(?:href|src)=\"(?<url>/[^\"]+\\.(?:css|js))\"", RegexOptions.CultureInvariant)]
    private static partial Regex AssetReference();

    private async Task<string> SignedInCookieAsync(long telegramUserId, string name)
    {
        using HttpClient browser = Browser(site.Factory);
        using HttpResponseMessage callback = await TelegramSignInDriver.SignInAsync(
            browser, site.Telegram, new FakeTelegramIdentity(telegramUserId, name, null), "/en", Ct);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        return $"{AuthCookieNames.Session}={SessionCookieValue(callback)}";
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string? cookie, string? acceptEncoding)
    {
        using HttpClient client = Bare(site.Factory);
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        if (acceptEncoding is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        }

        return await client.SendAsync(request, Ct);
    }

    [Theory]
    [InlineData("br")]
    [InlineData("identity")]
    [InlineData(null)]
    public async Task SignedInHtml_IsNotCompressed(string? acceptEncoding)
    {
        string cookie = await SignedInCookieAsync(540001, "Breach Guarded");

        using HttpResponseMessage response = await GetAsync(Page, cookie, acceptEncoding);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Content.Headers.ContentEncoding);
        string body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("Breach Guarded", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnonymousHtml_IsCompressedWithBr()
    {
        using HttpResponseMessage response = await GetAsync(Page, cookie: null, acceptEncoding: "br");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("br", response.Content.Headers.ContentEncoding);
    }

    [Fact]
    public async Task SignedInHtml_IsPrivateNoStore()
    {
        string cookie = await SignedInCookieAsync(540002, "No Store");

        using HttpResponseMessage response = await GetAsync(Page, cookie, acceptEncoding: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // HttpClient parses the header into directives and prints them in its own order.
        Assert.True(response.Headers.CacheControl?.Private == true);
        Assert.True(response.Headers.CacheControl?.NoStore == true);
    }

    [Fact]
    public async Task AnonymousHtml_HasNoPrivateNoStore()
    {
        using HttpResponseMessage response = await GetAsync(Page, cookie: null, acceptEncoding: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string cacheControl = response.Headers.TryGetValues("Cache-Control", out IEnumerable<string>? values) ? string.Join(", ", values) : string.Empty;
        Assert.DoesNotContain("no-store", cacheControl, StringComparison.Ordinal);
        Assert.DoesNotContain("private", cacheControl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignedIn_StaticAssets_KeepImmutableCacheControl()
    {
        string cookie = await SignedInCookieAsync(540003, "Assets");
        using HttpResponseMessage page = await GetAsync(Page, cookie, acceptEncoding: null);
        string body = await page.Content.ReadAsStringAsync(Ct);
        string[] assets = [.. AssetReference().Matches(body).Select(m => m.Groups["url"].Value).Distinct(StringComparer.Ordinal)];
        string css = assets.First(a => a.EndsWith(".css", StringComparison.Ordinal));
        string script = assets.First(a => a.EndsWith(".js", StringComparison.Ordinal));

        foreach (string asset in new[] { css, script })
        {
            using HttpResponseMessage signedIn = await GetAsync(asset, cookie, acceptEncoding: null);
            using HttpResponseMessage anonymous = await GetAsync(asset, cookie: null, acceptEncoding: null);

            Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
            string signedInHeader = string.Join(", ", signedIn.Headers.GetValues("Cache-Control"));
            Assert.DoesNotContain("no-store", signedInHeader, StringComparison.Ordinal);
            Assert.DoesNotContain("private", signedInHeader, StringComparison.Ordinal);
            // A published app serves fingerprinted assets as immutable; the unpublished test host may not, so the
            // signed-in response is compared with the anonymous one rather than with a literal.
            Assert.True(
                string.Equals(string.Join(", ", anonymous.Headers.GetValues("Cache-Control")), signedInHeader, StringComparison.Ordinal),
                $"{asset}: Cache-Control differs for a signed-in user ({signedInHeader}).");
        }
    }

    [Theory]
    [InlineData("/api/antiforgery", true, false, HttpsCompressionMode.DoNotCompress)]
    [InlineData("/api/antiforgery", false, false, HttpsCompressionMode.DoNotCompress)]
    [InlineData("/API/antiforgery", true, false, HttpsCompressionMode.DoNotCompress)]
    [InlineData("/api/Antiforgery", false, false, HttpsCompressionMode.DoNotCompress)]
    [InlineData("/api/antiforgery/", true, false, HttpsCompressionMode.DoNotCompress)]
    [InlineData("/API/ANTIFORGERY/", false, false, HttpsCompressionMode.DoNotCompress)]
    [InlineData("/api/antiforgery/extra", true, false, HttpsCompressionMode.Default)]
    [InlineData("/api/antiforgeryx", true, false, HttpsCompressionMode.Default)]
    [InlineData("/api/answers", true, false, HttpsCompressionMode.Default)]
    [InlineData("/en/dotnet/practice", true, true, HttpsCompressionMode.DoNotCompress)]
    [InlineData("/en/dotnet/practice", false, true, HttpsCompressionMode.Default)]
    [InlineData("/en/dotnet/practice", true, false, HttpsCompressionMode.Default)]
    public async Task BreachGuard_DecidesByEndpointAndUser(string path, bool authenticated, bool razorPage, HttpsCompressionMode expected)
    {
        DefaultHttpContext context = new();
        context.Request.Path = path;
        if (razorPage)
        {
            context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new ComponentTypeMetadata(typeof(object))), "page"));
        }

        if (authenticated)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "x")], "test"));
        }

        FakeCompressionFeature feature = new();
        context.Features.Set<IHttpsCompressionFeature>(feature);
        bool nextCalled = false;
        BreachGuardMiddleware middleware = new(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(expected, feature.Mode);
    }

    [Fact]
    public async Task BreachGuard_WithoutCompressionFeature_DoesNotFail()
    {
        // The compression middleware installs its feature only when the client accepts an encoding.
        DefaultHttpContext context = new();
        context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new ComponentTypeMetadata(typeof(object))), "page"));
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "x")], "test"));
        BreachGuardMiddleware middleware = new(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        Assert.Null(context.Features.Get<IHttpsCompressionFeature>());
    }

    private sealed class FakeCompressionFeature : IHttpsCompressionFeature
    {
        public HttpsCompressionMode Mode { get; set; } = HttpsCompressionMode.Default;
    }
}
