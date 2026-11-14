using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Trainer.TestSupport;
using Trainer.Web.Auth;
using static Trainer.Web.Tests.SignInTestHelpers;

namespace Trainer.Web.Tests;

/// <summary><c>/signin</c>, the access-denied redirect and an unconfigured provider, on a site without Telegram credentials.</summary>
public sealed class SignInRedirectTests(WebSiteFixture site, PostgresFixture postgres) : IClassFixture<WebSiteFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpResponseMessage> GetAsync(string path, string? cookie = null)
    {
        using HttpClient client = Bare(site.Factory);
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return await client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task SignInRedirect_PreservesDeniedAndUsesReturnPathCulture()
    {
        using HttpResponseMessage response = await GetAsync("/signin?denied=1&ReturnUrl=%2Fru%2Fadmin", "lang=en");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/ru/signin?denied=1&returnUrl=%2Fru%2Fadmin", response.Headers.Location?.OriginalString);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("noindex", Assert.Single(response.Headers.GetValues("X-Robots-Tag")));
    }

    [Theory]
    [InlineData("/ru/вопросы?q=замыкание", "/ru/%D0%B2%D0%BE%D0%BF%D1%80%D0%BE%D1%81%D1%8B?q=%D0%B7%D0%B0%D0%BC%D1%8B%D0%BA%D0%B0%D0%BD%D0%B8%D0%B5")]
    [InlineData("/ru/dotnet/questions?level=middle", "/ru/dotnet/questions?level=middle")]
    [InlineData("/", "/")]
    // The unsafe branches of the hook (library 1.1.1 refuses these first, so the hook is only reached directly here).
    [InlineData("/en/\tx", "/en")]
    [InlineData("/en//evil.test", "/en")]
    [InlineData("/en/\\evil.test", "/en")]
    [InlineData("/ru/x\n", "/ru")]
    [InlineData("/de/x", "/")]
    [InlineData("/\t/evil.test", "/")]
    [InlineData("//evil.test", "/")]
    [InlineData("https://evil.test", "/")]
    [InlineData("", "/")]
    [InlineData(null, "/")]
    public void ResolveReturnPathAfterSignIn_ProducesAnAsciiHeaderValue(string? candidate, string expected)
    {
        Assert.Equal(expected, SignInRedirectEndpoint.ResolveReturnPathAfterSignIn(candidate));
    }

    [Fact]
    public async Task SignInRedirect_PreservesError()
    {
        using HttpResponseMessage response = await GetAsync("/signin?error=1", "lang=ru");

        Assert.Equal("/ru/signin?error=1", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("/signin?denied=0&error=0")]
    [InlineData("/signin?denied&error=")]
    [InlineData("/signin?denied=yes")]
    public async Task SignInRedirect_FlagsOtherThanOne_AreDropped(string path)
    {
        using HttpResponseMessage response = await GetAsync(path, "lang=ru");

        Assert.Equal("/ru/signin", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task SignInRedirect_WithoutReturnUrl_UsesLangCookie()
    {
        using HttpResponseMessage response = await GetAsync("/signin", "lang=ru");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/ru/signin", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("https://evil.test/x")]
    [InlineData("//evil.test")]
    [InlineData("/\\evil.test")]
    [InlineData("%2F%2Fevil.test")]
    [InlineData("/ru%0d%0aX")]
    public async Task SignInRedirect_UnsafeReturnPaths_BecomeCultureHome(string rawReturnUrl)
    {
        using HttpResponseMessage response = await GetAsync("/signin?returnUrl=" + rawReturnUrl, "lang=ru");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/ru/signin?returnUrl=%2Fru", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task SignInRedirect_ReturnPathCultureWinsOverCookie()
    {
        using HttpResponseMessage response = await GetAsync("/signin?returnUrl=%2Fen%2Fx", "lang=ru");

        Assert.Equal("/en/signin?returnUrl=%2Fen%2Fx", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Challenge_Unconfigured_Returns503()
    {
        using HttpResponseMessage response = await GetAsync("/signin-telegram/challenge?returnUrl=%2Fen");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task SignInPage_Unconfigured_SaysSoInsteadOfLinkingToA503()
    {
        using HttpResponseMessage response = await GetAsync("/en/signin");
        string body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Sign-in is not configured", body, StringComparison.Ordinal);
        Assert.DoesNotContain("/signin-telegram/challenge", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forbidden_RedirectsToDeniedWithTheOriginalPathAndQuery()
    {
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions
        {
            ConfigureServices = services => services.AddTransient<IStartupFilter, ForbidProbe>(),
        });
        using HttpClient client = Bare(factory);

        using HttpResponseMessage response = await client.GetAsync("/probe/forbid?x=1&y=%2F", Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/signin?denied=1&ReturnUrl=%2Fprobe%2Fforbid%3Fx%3D1%26y%3D%252F", response.Headers.Location?.OriginalString);
    }

    /// <summary>Answers <c>/probe/forbid</c> with a forbid on the session scheme, as an authorization failure would.</summary>
    private sealed class ForbidProbe : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (HttpContext context, RequestDelegate nextMiddleware) =>
            {
                if (context.Request.Path == "/probe/forbid")
                {
                    await context.ForbidAsync(TelegramAuth.CookieScheme);
                    return;
                }

                await nextMiddleware(context);
            });
            next(app);
        };
    }
}
