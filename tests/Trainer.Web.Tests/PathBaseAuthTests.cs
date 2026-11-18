using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Trainer.TestSupport;
using Trainer.Web.Auth;
using Trainer.Web.Seo;
using Trainer.Web.Startup;

namespace Trainer.Web.Tests;

/// <summary>
/// The server edition served under a path base (a reverse proxy prefix): sign-in redirects keep it, and the return
/// paths inside the server stay logical and local-only.
/// </summary>
public sealed class PathBaseAuthTests(PostgresFixture postgres) : IAsyncLifetime
{
    private TrainerWebFactory _factory = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = await TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions
        {
            ConfigureServices = services => services.AddTransient<IStartupFilter, ForbidProbe>(),
            Settings = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["App:PublicBaseUrl"] = "https://example.test/dit/",
            },
        });
        await _factory.WaitUntilReadyAsync();
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private static SitePaths ServerPaths(string baseUrl) =>
        new(Options.Create(new AppOptions { PublicBaseUrl = baseUrl, Edition = SiteEdition.Server }));

    private async Task<HttpResponseMessage> GetAsync(string path, string? cookie = null)
    {
        using HttpClient client = _factory.CreateNonRedirectingClient();
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return await client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task AnonymousAdmin_RedirectsToTheSignInUnderThePathBase_WithALogicalReturnUrl()
    {
        using HttpResponseMessage response = await GetAsync("/dit/ru/admin");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/dit/signin?ReturnUrl=%2Fru%2Fadmin", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Forbidden_RedirectsToDeniedUnderThePathBase_WithALogicalReturnUrl()
    {
        using HttpResponseMessage response = await GetAsync("/dit/probe/forbid?x=1&y=%2F");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/dit/signin?denied=1&ReturnUrl=%2Fprobe%2Fforbid%3Fx%3D1%26y%3D%252F", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task SignInRedirect_ForwardsToTheCulturePageUnderThePathBase()
    {
        using HttpResponseMessage response = await GetAsync("/dit/signin?denied=1&ReturnUrl=%2Fru%2Fadmin", "lang=en");

        Assert.Equal("/dit/ru/signin?denied=1&returnUrl=%2Fru%2Fadmin", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("%2F%2Fevil.test")]
    [InlineData("https%3A%2F%2Fevil.test%2Fru")]
    [InlineData("%2Fru%2F%5Cevil.test")]
    [InlineData("%2Fdit%2Fru%2Fx")]
    public async Task SignInRedirect_KeepsRejectingReturnPathsThatAreNotLocalToACulture(string rawReturnUrl)
    {
        using HttpResponseMessage response = await GetAsync("/dit/signin?returnUrl=" + rawReturnUrl, "lang=ru");

        Assert.Equal("/dit/ru/signin?returnUrl=%2Fru", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("/ru/dotnet/questions?level=middle", "/dit/ru/dotnet/questions?level=middle")]
    [InlineData("/", "/dit/")]
    [InlineData("/en//evil.test", "/dit/en")]
    [InlineData("/en/\\evil.test", "/dit/en")]
    [InlineData("//evil.test", "/dit/")]
    [InlineData("/de/x", "/dit/")]
    [InlineData(null, "/dit/")]
    public void ResolveReturnLocationAfterSignIn_PrefixesOnlyWhatPassesTheSameValidation(string? candidate, string expected) =>
        Assert.Equal(expected, SignInRedirectEndpoint.ResolveReturnLocationAfterSignIn(candidate, ServerPaths("https://example.test/dit/")));

    [Fact]
    public void ResolveReturnLocationAfterSignIn_WithoutAPathBase_IsTheValidatedPath() =>
        Assert.Equal("/ru/x", SignInRedirectEndpoint.ResolveReturnLocationAfterSignIn("/ru/x", ServerPaths("https://example.test")));

    [Theory]
    [InlineData("/dit/signin?ReturnUrl=%2Fdit%2Fen%2Fx%3Fa%3D1", "/dit", "ReturnUrl", "/dit/signin?ReturnUrl=%2Fen%2Fx%3Fa%3D1")]
    [InlineData("/dit/signin?ReturnUrl=%2Fdit", "/dit", "ReturnUrl", "/dit/signin?ReturnUrl=%2F")]
    [InlineData("/dit/signin?ReturnUrl=%2Fdity%2Fx", "/dit", "ReturnUrl", "/dit/signin?ReturnUrl=%2Fdity%2Fx")]
    [InlineData("/dit/signin?ReturnUrl=%2Fen%2Fx", "/dit", "ReturnUrl", "/dit/signin?ReturnUrl=%2Fen%2Fx")]
    [InlineData("/signin?ReturnUrl=%2Fen%2Fx", "", "ReturnUrl", "/signin?ReturnUrl=%2Fen%2Fx")]
    [InlineData("/dit/signin", "/dit", "ReturnUrl", "/dit/signin")]
    public void StripPathBaseFromReturnUrl_RemovesOnlyAWholePathBaseSegment(string target, string pathBase, string parameter, string expected) =>
        Assert.Equal(expected, TrainerCookieEvents.StripPathBaseFromReturnUrl(target, new PathString(pathBase), parameter));

    private sealed class ForbidProbe : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (HttpContext context, RequestDelegate nextMiddleware) =>
            {
                // Runs before the host's UsePathBase, so it applies the path base itself, as that middleware would.
                if (context.Request.Path.StartsWithSegments("/dit", out PathString rest) && rest == "/probe/forbid")
                {
                    context.Request.PathBase = "/dit";
                    context.Request.Path = rest;
                    await context.ForbidAsync(TelegramAuth.CookieScheme);
                    return;
                }

                await nextMiddleware(context);
            });
            next(app);
        };
    }
}
