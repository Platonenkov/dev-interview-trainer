using System.Globalization;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Trainer.Application.Users;
using Trainer.Domain.Keys;
using Trainer.Domain.Users;
using Trainer.TestSupport;
using Trainer.Web.Auth;
using Trainer.Web.Caching;
using Trainer.Web.Routing;
using static Trainer.Web.Tests.SignInTestHelpers;

namespace Trainer.Web.Tests;

public sealed class SignInTests(SignInSiteFixture site) : IClassFixture<SignInSiteFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<HttpResponseMessage> SignInAsync(HttpClient client, FakeTelegramIdentity who, string returnUrl = "/ru/dotnet/questions") =>
        TelegramSignInDriver.SignInAsync(client, site.Telegram, who, returnUrl, Ct);

    private async Task<T> WithUsersAsync<T>(Func<IUserAccountService, Task<T>> action)
    {
        await using AsyncServiceScope scope = site.Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<IUserAccountService>());
    }

    private async Task<UserAccess> CreateUserAsync(long telegramUserId, string name, CultureKey? preferred = null, bool blocked = false)
    {
        UserAccess created = await WithUsersAsync(users => users.SignInAsync(new TelegramProfile(telegramUserId, name.ToLowerInvariant(), name), Ct));
        if (preferred is { } culture)
        {
            await WithUsersAsync(async users =>
            {
                await users.SetPreferredCultureAsync(created.UserId, culture, Ct);
                return true;
            });
        }

        if (blocked)
        {
            await WithUsersAsync(users => users.SetBlockedAsync(created.UserId, blocked: true, Ct));
        }

        return created;
    }

    [Fact]
    public async Task SignIn_NewTelegramUser_CreatesUserAndRedirectsToReturnUrl()
    {
        using HttpClient client = Browser(site.Factory);

        using HttpResponseMessage response = await SignInAsync(client, new FakeTelegramIdentity(510001, "New User", "new_user"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/ru/dotnet/questions", response.Headers.Location?.OriginalString);
        UserAccess? access = await WithUsersAsync(users => users.GetAccessAsync(510001, Ct));
        Assert.NotNull(access);
        Assert.Equal("New User", access.DisplayName);
        Assert.Equal("new_user", access.Username);
    }

    [Fact]
    public async Task SignIn_SessionCookieIsPersistentThirtyDays()
    {
        using HttpClient client = Browser(site.Factory);
        DateTimeOffset before = DateTimeOffset.UtcNow;

        using HttpResponseMessage response = await SignInAsync(client, new FakeTelegramIdentity(510002, "Persistent", "persistent"));

        string cookie = SetCookie(response, AuthCookieNames.Session) ?? throw new InvalidOperationException("no session cookie");
        string[] attributes = [.. cookie.Split(';').Skip(1).Select(a => a.Trim())];
        string expires = attributes.Single(a => a.StartsWith("expires=", StringComparison.OrdinalIgnoreCase))["expires=".Length..];
        DateTimeOffset expiresAt = DateTimeOffset.Parse(expires, CultureInfo.InvariantCulture);
        Assert.InRange(expiresAt, before.AddDays(30).AddMinutes(-2), DateTimeOffset.UtcNow.AddDays(30).AddMinutes(2));
        Assert.Contains("secure", attributes, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("httponly", attributes, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", attributes, StringComparer.OrdinalIgnoreCase);
        AuthenticationProperties? properties = SessionCookieReader.ReadProperties(site.Factory, response);
        Assert.True(properties?.IsPersistent);
    }

    [Fact]
    public async Task SignIn_IssuedPrincipal_CarriesTelegramIdUserIdStampAndNoRole()
    {
        using HttpClient client = Browser(site.Factory);

        using HttpResponseMessage response = await SignInAsync(client, new FakeTelegramIdentity(510003, "Plain", "plain"));

        ClaimsPrincipal principal = SessionCookieReader.Read(site.Factory, response) ?? throw new InvalidOperationException("no ticket");
        UserAccess access = (await WithUsersAsync(users => users.GetAccessAsync(510003, Ct)))!;
        Assert.Equal("510003", principal.FindFirst(TrainerClaimTypes.TelegramUserId)?.Value);
        Assert.Equal(access.UserId.Value, principal.FindFirst(TrainerClaimTypes.UserId)?.Value);
        Assert.Equal(access.SecurityStamp, principal.FindFirst(TrainerClaimTypes.SecurityStamp)?.Value);
        Assert.Empty(principal.FindAll(ClaimTypes.Role));
        Assert.Equal(access.UserId, principal.GetUserId());
        Assert.Equal(510003L, principal.GetTelegramUserId());
        Assert.Equal("Plain", principal.GetDisplayName());
    }

    [Fact]
    public async Task SignIn_ConfiguredAdmin_HasAdminRole()
    {
        using HttpClient client = Browser(site.Factory);

        using HttpResponseMessage response = await SignInAsync(client, new FakeTelegramIdentity(SignInSiteFixture.AdminTelegramId, "Admin", "admin"));

        ClaimsPrincipal principal = SessionCookieReader.Read(site.Factory, response) ?? throw new InvalidOperationException("no ticket");
        Assert.True(principal.IsInRole(Roles.Admin));
        Assert.Equal([Roles.Admin], principal.FindAll(ClaimTypes.Role).Select(c => c.Value));
    }

    [Fact]
    public async Task SignIn_BlockedUser_RedirectsToDeniedAndSetsNoSessionCookie()
    {
        await CreateUserAsync(510004, "Blocked", blocked: true);
        using HttpClient client = Browser(site.Factory);

        using HttpResponseMessage response = await SignInAsync(client, new FakeTelegramIdentity(510004, "Blocked", "blocked"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/signin?denied=1", response.Headers.Location?.OriginalString);
        Assert.Null(SetCookie(response, AuthCookieNames.Session));
    }

    [Fact]
    public async Task SignIn_RemoteFailure_RedirectsToError()
    {
        using HttpClient client = Browser(site.Factory);
        site.Telegram.FailNext = true;

        using HttpResponseMessage response = await SignInAsync(client, new FakeTelegramIdentity(510005, "Failed", "failed"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/signin?error=1", response.Headers.Location?.OriginalString);
        Assert.Null(SetCookie(response, AuthCookieNames.Session));
        Assert.Null(await WithUsersAsync(users => users.GetAccessAsync(510005, Ct)));
    }

    // The challenge endpoint belongs to the library and is reachable directly, so these values reach it unvalidated.
    // The driver percent-encodes them: "\t" arrives as a real tab, "/%09/..." as literal text.
    [Theory]
    [InlineData("//evil.test", "/")]
    [InlineData("/\t/evil.test", "/")]
    [InlineData("/\n/evil.test", "/")]
    [InlineData("/\r\n/evil.test", "/")]
    [InlineData("/%09/evil.test", "/")]
    [InlineData("/%0a/evil.test", "/")]
    [InlineData("/\\evil.test", "/")]
    [InlineData("https://evil.test", "/")]
    // Telegram.OpenIdConnect.AspNetCore 1.1.1 refuses these itself and falls back to "/"; the OnTicketReceived hook
    // stays as defence in depth (its unsafe branches: ResolveReturnPathAfterSignIn_ProducesAnAsciiHeaderValue).
    [InlineData("/en/\tx", "/")]
    [InlineData("/en//evil.test", "/")]
    [InlineData("/x", "/")]
    [InlineData("/", "/")]
    [InlineData("/en/x?y", "/en/x?y")]
    [InlineData("/ru/dotnet/questions?level=middle", "/ru/dotnet/questions?level=middle")]
    public async Task SignIn_UnsafeReturnUrl_FallsBackToDefault(string returnUrl, string expected)
    {
        using HttpClient client = Browser(site.Factory);

        using HttpResponseMessage response = await SignInAsync(client, new FakeTelegramIdentity(510006, "Unsafe", "unsafe"), returnUrl);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(expected, RawLocation(response));
    }

    [Fact]
    public async Task SignIn_WithLangCookie_StoresPreferredCulture()
    {
        using HttpClient client = Browser(site.Factory);
        using (HttpResponseMessage choose = await client.GetAsync("/lang/ru?return=%2Fru", Ct))
        {
            Assert.NotNull(SetCookie(choose, RootRedirectEndpoint.CookieName));
        }

        using HttpResponseMessage response = await SignInAsync(client, new FakeTelegramIdentity(510007, "Ru", "ru_user"));

        UserAccess? access = await WithUsersAsync(users => users.GetAccessAsync(510007, Ct));
        Assert.Equal(new CultureKey("ru"), access?.PreferredCulture);
        Assert.Null(SetCookie(response, RootRedirectEndpoint.CookieName));
    }

    [Fact]
    public async Task SignIn_NewDeviceWithoutLangCookie_SetsLangFromPreference()
    {
        await CreateUserAsync(510008, "Device", new CultureKey("ru"));
        using HttpClient client = Browser(site.Factory);

        using HttpResponseMessage response = await SignInAsync(client, new FakeTelegramIdentity(510008, "Device", "device"));

        string cookie = SetCookie(response, RootRedirectEndpoint.CookieName) ?? throw new InvalidOperationException("no lang cookie");
        Assert.StartsWith("lang=ru;", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Root_SignedIn_KeepsSpecRedirectOrder()
    {
        await CreateUserAsync(510009, "Order", new CultureKey("ru"));
        using HttpClient browser = Browser(site.Factory);
        using HttpResponseMessage signIn = await SignInAsync(browser, new FakeTelegramIdentity(510009, "Order", "order"));
        string session = $"{AuthCookieNames.Session}={SessionCookieValue(signIn)}";
        using HttpClient bare = Bare(site.Factory);

        // The stored preference (ru) never outranks the request: no cookie means Accept-Language decides.
        Assert.Equal("/en", await RootLocationAsync(bare, session, "en"));
        Assert.Equal("/en", await RootLocationAsync(bare, session + "; lang=en", "ru"));
        Assert.Equal("/ru", await RootLocationAsync(bare, session, "ru"));
    }

    private static async Task<string?> RootLocationAsync(HttpClient client, string cookies, string acceptLanguage)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/");
        request.Headers.Add("Cookie", cookies);
        request.Headers.Add("Accept-Language", acceptLanguage);
        using HttpResponseMessage response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location?.OriginalString;
    }
}
