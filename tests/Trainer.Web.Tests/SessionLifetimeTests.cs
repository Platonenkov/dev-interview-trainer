using System.Globalization;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Time.Testing;
using Microsoft.EntityFrameworkCore;
using Trainer.Application.Users;
using Trainer.Contracts;
using Trainer.Infrastructure.Persistence;
using Trainer.Domain.Users;
using Trainer.TestSupport;
using Trainer.Web.Auth;
using Trainer.Web.Caching;
using static Trainer.Web.Tests.SignInTestHelpers;

namespace Trainer.Web.Tests;

/// <summary>Sliding expiry, the five-minute revalidation against the database and what a refresh keeps.</summary>
public sealed class SessionLifetimeTests(PostgresFixture postgres)
{
    private const long UserId = 530001;

    private static readonly TimeSpan JustInside = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan JustOutside = TimeSpan.FromMinutes(6);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakeTelegramIdentity Person(long id = UserId, string name = "Session Person") => new(id, name, "session_person");

    private static async Task<string> GetBodyAsync(HttpClient client, string path = "/en")
    {
        using HttpResponseMessage response = await client.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    private static bool IsSignedInPage(string body, string name) =>
        body.Contains($">{name}</a>", StringComparison.Ordinal) && !body.Contains(">Sign in</a>", StringComparison.Ordinal);

    private static bool DeletesSession(HttpResponseMessage response) =>
        SetCookie(response, AuthCookieNames.Session) is { } cookie
        && cookie.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase);

    private static async Task SetRolesAsync(SessionSite site, UserAccess access, params string[] roles) =>
        await site.WithUsersAsync(async users =>
        {
            await users.SetRolesAsync(access.UserId, roles, Ct);
            return true;
        });

    [Fact]
    public async Task SessionCookie_SlidingRenewal()
    {
        await using SessionSite site = await SessionSite.StartAsync(postgres);
        (HttpClient client, HttpResponseMessage callback) = await site.SignInAsync(Person(), Ct);
        using HttpClient disposableClient = client;
        callback.Dispose();

        site.Clock.Advance(TimeSpan.FromDays(20));
        using HttpResponseMessage response = await client.GetAsync("/en", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AuthenticationProperties properties = SessionCookieReader.ReadProperties(site.Factory, response)
            ?? throw new InvalidOperationException("The session cookie was not renewed.");
        DateTimeOffset now = site.Clock.GetUtcNow();
        Assert.InRange(properties.ExpiresUtc ?? default, now.AddDays(30).AddMinutes(-2), now.AddDays(30).AddMinutes(2));
        Assert.InRange(properties.IssuedUtc ?? default, now.AddMinutes(-2), now.AddMinutes(2));
        Assert.True(properties.IsPersistent);
    }

    [Fact]
    public async Task BlockedUser_IsSignedOutAfterRevalidationInterval()
    {
        await using SessionSite site = await SessionSite.StartAsync(postgres);
        (HttpClient client, HttpResponseMessage callback) = await site.SignInAsync(Person(), Ct);
        using HttpClient disposableClient = client;
        callback.Dispose();
        UserAccess access = await site.AccessOfAsync(UserId, Ct);
        await site.WithUsersAsync(users => users.SetBlockedAsync(access.UserId, blocked: true, Ct));

        site.Clock.Advance(JustInside);
        Assert.True(IsSignedInPage(await GetBodyAsync(client), "Session Person"));

        site.Clock.Advance(JustOutside - JustInside);
        using HttpResponseMessage response = await client.GetAsync("/en", Ct);
        string body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(">Sign in</a>", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Session Person", body, StringComparison.Ordinal);
        Assert.True(DeletesSession(response), "The session cookie should be deleted.");
    }

    [Fact]
    public async Task RoleChange_RefreshedAfterInterval()
    {
        await using SessionSite site = await SessionSite.StartAsync(postgres);
        (HttpClient client, HttpResponseMessage callback) = await site.SignInAsync(Person(), Ct);
        using HttpClient disposableClient = client;
        callback.Dispose();
        UserAccess access = await site.AccessOfAsync(UserId, Ct);
        await SetRolesAsync(site, access, Roles.Editor);

        site.Clock.Advance(JustInside);
        using (HttpResponseMessage inside = await client.GetAsync("/en", Ct))
        {
            // Nothing is checked inside the interval, so nothing is reissued either.
            Assert.Null(SetCookie(inside, AuthCookieNames.Session));
        }

        site.Clock.Advance(JustOutside - JustInside);
        using HttpResponseMessage outside = await client.GetAsync("/en", Ct);

        ClaimsPrincipal refreshed = SessionCookieReader.Read(site.Factory, outside)
            ?? throw new InvalidOperationException("The session cookie was not reissued.");
        Assert.Equal([Roles.Editor], refreshed.FindAll(ClaimTypes.Role).Select(c => c.Value));
        UserAccess current = await site.AccessOfAsync(UserId, Ct);
        Assert.Equal(current.SecurityStamp, refreshed.FindFirst(TrainerClaimTypes.SecurityStamp)?.Value);
        Assert.Equal(access.UserId.Value, refreshed.FindFirst(TrainerClaimTypes.UserId)?.Value);
    }

    [Fact]
    public async Task RoleRefresh_KeepsSubAndAntiforgeryTokenStillValid()
    {
        await using SessionSite site = await SessionSite.StartAsync(postgres);
        (HttpClient client, HttpResponseMessage callback) = await site.SignInAsync(Person(), Ct);
        using HttpClient disposableClient = client;
        using HttpResponseMessage signedIn = callback;
        ClaimsPrincipal original = SessionCookieReader.Read(site.Factory, signedIn) ?? throw new InvalidOperationException("no ticket");
        string tokenBeforeRefresh = AntiforgeryToken(await GetBodyAsync(client));
        await SetRolesAsync(site, await site.AccessOfAsync(UserId, Ct), Roles.Editor);
        site.Clock.Advance(JustOutside);

        using HttpResponseMessage refresh = await client.GetAsync("/en", Ct);
        ClaimsPrincipal refreshed = SessionCookieReader.Read(site.Factory, refresh)
            ?? throw new InvalidOperationException("The session cookie was not reissued.");
        Assert.Contains(Roles.Editor, refreshed.FindAll(ClaimTypes.Role).Select(c => c.Value));
        string? subject = original.FindFirst(TrainerClaimTypes.Subject)?.Value;
        Assert.False(string.IsNullOrEmpty(subject));
        Assert.Equal(subject, refreshed.FindFirst(TrainerClaimTypes.Subject)?.Value);
        Assert.Equal(original.FindFirst(TrainerClaimTypes.TelegramUserId)?.Value, refreshed.FindFirst(TrainerClaimTypes.TelegramUserId)?.Value);
        Assert.Equal(original.Identity?.AuthenticationType, refreshed.Identity?.AuthenticationType);

        using FormUrlEncodedContent form = new([new KeyValuePair<string, string>("__RequestVerificationToken", tokenBeforeRefresh)]);
        using HttpResponseMessage logout = await client.PostAsync("/signin-telegram/logout", form, Ct);

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.True(DeletesSession(logout), "The logout with the old token should have cleared the session.");
    }

    [Fact]
    public async Task WithinInterval_NoDatabaseCheck()
    {
        await using SessionSite site = await SessionSite.StartAsync(postgres);
        (HttpClient client, HttpResponseMessage callback) = await site.SignInAsync(Person(), Ct);
        using HttpClient disposableClient = client;
        callback.Dispose();
        site.Calls.Reset();

        for (int i = 0; i < 10; i++)
        {
            site.Clock.Advance(TimeSpan.FromSeconds(25));
            Assert.True(IsSignedInPage(await GetBodyAsync(client), "Session Person"));
        }

        Assert.Equal(0, site.Calls.GetAccess);

        site.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(IsSignedInPage(await GetBodyAsync(client), "Session Person"));
        Assert.Equal(1, site.Calls.GetAccess);

        // The check is remembered in the reissued cookie, so the next request is free again.
        Assert.True(IsSignedInPage(await GetBodyAsync(client), "Session Person"));
        Assert.Equal(1, site.Calls.GetAccess);
    }

    [Fact]
    public async Task ConfiguredAdminIds_ReadAtStartup()
    {
        const long adminId = 77;
        FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
        await using FakeTelegramOidcServer telegram = await FakeTelegramOidcServer.StartAsync(TrainerWebFactoryOptions.TelegramClientId);
        string connectionString = await postgres.CreateDatabaseAsync();

        string sessionCookie;
        await using (SessionSite withAdmin = await SessionSite.StartAsync(
            postgres,
            new Dictionary<string, string?> { ["App:AdminTelegramIds"] = adminId.ToString(CultureInfo.InvariantCulture) },
            connectionString,
            telegram,
            clock))
        {
            (HttpClient client, HttpResponseMessage callback) = await withAdmin.SignInAsync(Person(adminId, "Configured Admin"), Ct);
            using HttpClient disposableClient = client;
            using HttpResponseMessage signedIn = callback;
            ClaimsPrincipal principal = SessionCookieReader.Read(withAdmin.Factory, signedIn) ?? throw new InvalidOperationException("no ticket");
            Assert.True(principal.IsInRole(Roles.Admin));
            sessionCookie = $"{AuthCookieNames.Session}={SessionCookieValue(signedIn)}";
        }

        clock.Advance(JustOutside);
        await using SessionSite withoutAdmin = await SessionSite.StartAsync(postgres, settings: null, connectionString, telegram, clock);
        using HttpClient bare = Bare(withoutAdmin.Factory);
        using HttpRequestMessage request = new(HttpMethod.Get, "/en");
        request.Headers.Add("Cookie", sessionCookie);

        using HttpResponseMessage response = await bare.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ClaimsPrincipal refreshed = SessionCookieReader.Read(withoutAdmin.Factory, response)
            ?? throw new InvalidOperationException("The session cookie was not reissued.");
        Assert.False(refreshed.IsInRole(Roles.Admin));
        Assert.Empty(refreshed.FindAll(ClaimTypes.Role));
    }

    private static async Task<HttpResponseMessage> GetWithCookieAsync(HttpClient client, string path, string cookie)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request, Ct);
    }

    private static bool IsPrivateNoStore(HttpResponseMessage response) =>
        response.Headers.CacheControl is { Private: true, NoStore: true };

    private static async Task<string> FirstStylesheetAsync(HttpClient client)
    {
        string body = await GetBodyAsync(client);
        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(body, "href=\"(/[^\"]+\\.css)\"");
        Assert.True(match.Success);
        return match.Groups[1].Value;
    }

    [Fact]
    public async Task SlidingRenewal_WithFreshLastCheck_ReissuesCookieWithoutDatabaseCheck()
    {
        await using SessionSite site = await SessionSite.StartAsync(postgres);
        (HttpClient client, HttpResponseMessage callback) = await site.SignInAsync(Person(), Ct);
        using HttpClient disposableClient = client;
        using HttpResponseMessage signedIn = callback;
        ClaimsPrincipal principal = SessionCookieReader.Read(site.Factory, signedIn) ?? throw new InvalidOperationException("no ticket");
        DateTimeOffset now = site.Clock.GetUtcNow();
        // Issued 20 days ago, last checked a minute ago: only the handler's own sliding rule can reissue this cookie.
        string cookie = site.ForgeSessionCookie(principal, now.AddDays(-20), now.AddDays(10), now.AddMinutes(-1));
        using HttpClient bare = Bare(site.Factory);
        site.Calls.Reset();

        using HttpResponseMessage response = await GetWithCookieAsync(bare, "/en", cookie);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, site.Calls.GetAccess);
        AuthenticationProperties renewed = SessionCookieReader.ReadProperties(site.Factory, response)
            ?? throw new InvalidOperationException("The session cookie was not renewed.");
        Assert.InRange(renewed.ExpiresUtc ?? default, now.AddDays(30).AddMinutes(-2), now.AddDays(30).AddMinutes(2));
        Assert.True(IsPrivateNoStore(response), "A page that reissues the cookie must stay private, no-store.");
    }

    [Fact]
    public async Task PageThatRefreshesTheCookie_IsPrivateNoStore()
    {
        await using SessionSite site = await SessionSite.StartAsync(postgres);
        (HttpClient client, HttpResponseMessage callback) = await site.SignInAsync(Person(), Ct);
        using HttpClient disposableClient = client;
        callback.Dispose();
        site.Clock.Advance(JustOutside);

        using HttpResponseMessage response = await client.GetAsync("/en", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(SetCookie(response, AuthCookieNames.Session));
        Assert.True(IsPrivateNoStore(response), $"Cache-Control was \"{response.Headers.CacheControl}\".");
    }

    [Fact]
    public async Task StaticAsset_AtRevalidationMoment_GetsNoCookieAndNoCacheOverride()
    {
        await using SessionSite site = await SessionSite.StartAsync(postgres);
        (HttpClient client, HttpResponseMessage callback) = await site.SignInAsync(Person(), Ct);
        using HttpClient disposableClient = client;
        callback.Dispose();
        string stylesheet = await FirstStylesheetAsync(client);
        using HttpClient bare = Bare(site.Factory);
        using HttpResponseMessage anonymous = await bare.GetAsync(stylesheet, Ct);
        site.Clock.Advance(JustOutside);
        site.Calls.Reset();

        using HttpResponseMessage asset = await client.GetAsync(stylesheet, Ct);

        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Null(SetCookie(asset, AuthCookieNames.Session));
        Assert.Equal(anonymous.Headers.CacheControl?.ToString(), asset.Headers.CacheControl?.ToString());
        Assert.False(asset.Headers.Contains("Pragma"));

        // The next page request does the refresh the asset skipped.
        using HttpResponseMessage page = await client.GetAsync("/en", Ct);
        Assert.NotNull(SetCookie(page, AuthCookieNames.Session));
    }

    [Fact]
    public async Task BlockedUser_IsStillRejectedOnAnAssetRequest()
    {
        await using SessionSite site = await SessionSite.StartAsync(postgres);
        (HttpClient client, HttpResponseMessage callback) = await site.SignInAsync(Person(), Ct);
        using HttpClient disposableClient = client;
        callback.Dispose();
        string stylesheet = await FirstStylesheetAsync(client);
        UserAccess access = await site.AccessOfAsync(UserId, Ct);
        await site.WithUsersAsync(users => users.SetBlockedAsync(access.UserId, blocked: true, Ct));
        site.Clock.Advance(JustOutside);

        using HttpResponseMessage asset = await client.GetAsync(stylesheet, Ct);

        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.True(DeletesSession(asset), "A blocked user's session ends on the first request after the interval, assets included.");
    }

    [Fact]
    public async Task DeletedUser_IsSignedOutAfterRevalidationInterval()
    {
        await using SessionSite site = await SessionSite.StartAsync(postgres);
        (HttpClient client, HttpResponseMessage callback) = await site.SignInAsync(Person(), Ct);
        using HttpClient disposableClient = client;
        callback.Dispose();
        await using (TrainerDbContext db = PostgresFixture.CreateContext(site.Factory.ConnectionString))
        {
            Assert.Equal(1, await db.Database.ExecuteSqlAsync($"DELETE FROM users WHERE telegram_user_id = {UserId}", Ct));
        }

        site.Clock.Advance(JustOutside);
        using HttpResponseMessage response = await client.GetAsync("/en", Ct);
        string body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Contains(">Sign in</a>", body, StringComparison.Ordinal);
        Assert.True(DeletesSession(response));
    }

    [Fact]
    public async Task TicketWithoutTelegramId_IsRejectedAtFirstCheck()
    {
        await using SessionSite site = await SessionSite.StartAsync(postgres);
        (HttpClient client, HttpResponseMessage callback) = await site.SignInAsync(Person(), Ct);
        using HttpClient disposableClient = client;
        using HttpResponseMessage signedIn = callback;
        ClaimsPrincipal principal = SessionCookieReader.Read(site.Factory, signedIn) ?? throw new InvalidOperationException("no ticket");
        ClaimsIdentity original = (ClaimsIdentity)principal.Identity!;
        ClaimsIdentity stripped = new(
            original.Claims.Where(c => c.Type != TrainerClaimTypes.TelegramUserId), original.AuthenticationType, original.NameClaimType, original.RoleClaimType);
        DateTimeOffset now = site.Clock.GetUtcNow();
        string cookie = site.ForgeSessionCookie(new ClaimsPrincipal(stripped), now.AddMinutes(-10), now.AddDays(30), lastChecked: null);
        using HttpClient bare = Bare(site.Factory);

        using HttpResponseMessage response = await GetWithCookieAsync(bare, "/en", cookie);
        string body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Contains(">Sign in</a>", body, StringComparison.Ordinal);
        Assert.True(DeletesSession(response));
    }

    [Fact]
    public async Task DatabaseFailureDuringRevalidation_RejectsTheRequestButKeepsTheCookie()
    {
        await using SessionSite site = await SessionSite.StartAsync(postgres);
        (HttpClient client, HttpResponseMessage callback) = await site.SignInAsync(Person(), Ct);
        using HttpClient disposableClient = client;
        callback.Dispose();
        site.Clock.Advance(JustOutside);
        site.Calls.FailGetAccess = true;

        using (HttpResponseMessage failed = await client.GetAsync("/en", Ct))
        {
            string body = await failed.Content.ReadAsStringAsync(Ct);
            // Fail closed for this request: the page is rendered anonymously, never as the unverified user.
            Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
            Assert.Contains(">Sign in</a>", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Session Person", body, StringComparison.Ordinal);
            // A blip must not end the session: the cookie is neither deleted nor reissued.
            Assert.Null(SetCookie(failed, AuthCookieNames.Session));
        }

        site.Calls.FailGetAccess = false;
        Assert.True(IsSignedInPage(await GetBodyAsync(client), "Session Person"));
    }

    [Fact]
    public async Task DatabaseFailureDuringRevalidation_ApiAnswers503NotSignInRequired()
    {
        await using SessionSite site = await SessionSite.StartAsync(postgres);
        (HttpClient client, HttpResponseMessage callback) = await site.SignInAsync(Person(), Ct);
        using HttpClient disposableClient = client;
        callback.Dispose();
        string token = await ApiTestHelpers.GetTokenAsync(client, Ct);
        site.Clock.Advance(JustOutside);
        site.Calls.FailGetAccess = true;

        // The island client reads 401 as "the session ended" and stops syncing for the page's life; an outage must
        // look like an outage (5xx, retried with back-off) on every /api path a signed-in tab uses.
        using (HttpRequestMessage write = ApiTestHelpers.Write(
            HttpMethod.Post, "/api/answers/batch", ApiTestHelpers.Batch(ApiTestHelpers.Answer(ApiTestHelpers.ClosureQuestionId)), token, UserId))
        using (HttpResponseMessage response = await client.SendAsync(write, Ct))
        {
            await ApiTestHelpers.AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, ApiErrorCodes.ServerError, Ct);
            Assert.Null(SetCookie(response, AuthCookieNames.Session));
        }

        using (HttpRequestMessage read = new(HttpMethod.Post, "/api/sessions/practice")
        {
            Content = ApiTestHelpers.Json(ApiTestHelpers.Practice(count: 2), TrainerJsonContext.Default.PracticeSessionRequest),
        })
        {
            read.Headers.TryAddWithoutValidation(ApiTestHelpers.OwnerHeader, UserId.ToString(CultureInfo.InvariantCulture));
            using HttpResponseMessage response = await client.SendAsync(read, Ct);
            await ApiTestHelpers.AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, ApiErrorCodes.ServerError, Ct);
        }

        using (HttpResponseMessage response = await client.GetAsync("/api/profile?culture=en&stack=dotnet", Ct))
        {
            await ApiTestHelpers.AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, ApiErrorCodes.ServerError, Ct);
        }

        // A token issued now would belong to the anonymous principal and fail the user's next write.
        using (HttpResponseMessage response = await client.GetAsync("/api/antiforgery", Ct))
        {
            await ApiTestHelpers.AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, ApiErrorCodes.ServerError, Ct);
        }

        site.Calls.FailGetAccess = false;
        using HttpRequestMessage retry = ApiTestHelpers.Write(
            HttpMethod.Post, "/api/answers/batch", ApiTestHelpers.Batch(ApiTestHelpers.Answer(ApiTestHelpers.ClosureQuestionId)), token, UserId);
        using HttpResponseMessage recovered = await client.SendAsync(retry, Ct);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Fact]
    public async Task DatabaseFailureDuringRevalidation_SignInPageDoesNotPublishAnonymous()
    {
        await using SessionSite site = await SessionSite.StartAsync(postgres);
        (HttpClient client, HttpResponseMessage callback) = await site.SignInAsync(Person(), Ct);
        using HttpClient disposableClient = client;
        callback.Dispose();
        site.Clock.Advance(JustOutside);
        site.Calls.FailGetAccess = true;

        // Publishing "anon" would tell every island tab of the still signed-in user that the account changed.
        string body = await GetBodyAsync(client, "/en/signin");

        Assert.DoesNotContain("data-owner=\"anon\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FutureLastCheck_DoesNotSkipRevalidation()
    {
        await using FakeTelegramOidcServer telegram = await FakeTelegramOidcServer.StartAsync(TrainerWebFactoryOptions.TelegramClientId);
        string connectionString = await postgres.CreateDatabaseAsync();
        DateTimeOffset start = DateTimeOffset.UtcNow;
        FakeTimeProvider aheadClock = new(start);
        FakeTimeProvider behindClock = new(start);
        string cookie;
        await using (SessionSite ahead = await SessionSite.StartAsync(postgres, settings: null, connectionString, telegram, aheadClock))
        {
            (HttpClient client, HttpResponseMessage callback) = await ahead.SignInAsync(Person(), Ct);
            using HttpClient disposableClient = client;
            callback.Dispose();
            aheadClock.Advance(TimeSpan.FromHours(1));
            using HttpResponseMessage refreshed = await client.GetAsync("/en", Ct);
            cookie = $"{AuthCookieNames.Session}={SessionCookieValue(refreshed)}";
        }

        await using SessionSite behind = await SessionSite.StartAsync(postgres, settings: null, connectionString, telegram, behindClock);
        UserAccess access = await behind.AccessOfAsync(UserId, Ct);
        await behind.WithUsersAsync(users => users.SetBlockedAsync(access.UserId, blocked: true, Ct));
        behindClock.Advance(TimeSpan.FromMinutes(10));
        using HttpClient bare = Bare(behind.Factory);
        behind.Calls.Reset();

        // The cookie says it was last checked an hour in this clock's future; that must not mean "checked just now".
        using HttpResponseMessage response = await GetWithCookieAsync(bare, "/en", cookie);

        Assert.Equal(1, behind.Calls.GetAccess);
        Assert.True(DeletesSession(response));
    }

    [Fact]
    public async Task ParallelRequestsAfterTheInterval_AllSucceedAndStayBounded()
    {
        await using SessionSite site = await SessionSite.StartAsync(postgres);
        (HttpClient client, HttpResponseMessage callback) = await site.SignInAsync(Person(), Ct);
        using HttpClient disposableClient = client;
        using HttpResponseMessage signedIn = callback;
        string cookie = $"{AuthCookieNames.Session}={SessionCookieValue(signedIn)}";
        using HttpClient bare = Bare(site.Factory);
        site.Clock.Advance(JustOutside);
        site.Calls.Reset();
        const int parallel = 6;

        string[] bodies = await Task.WhenAll(Enumerable.Range(0, parallel).Select(async _ =>
        {
            using HttpResponseMessage response = await GetWithCookieAsync(bare, "/en", cookie);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsStringAsync(Ct);
        }));

        // Requests that arrive together each check once (each reissues the cookie, the last wins): the bound is the
        // number of concurrent requests, not one, and nobody is signed out by the race.
        Assert.All(bodies, body => Assert.True(IsSignedInPage(body, "Session Person")));
        Assert.InRange(site.Calls.GetAccess, 1, parallel);
    }
}
