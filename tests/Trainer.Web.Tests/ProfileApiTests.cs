using System.Net;
using Trainer.Application.Users;
using Trainer.Contracts;
using Trainer.TestSupport;
using static Trainer.Web.Tests.ApiTestHelpers;

namespace Trainer.Web.Tests;

/// <summary><c>GET /api/profile</c> and how <c>/api</c> treats a missing or ended session.</summary>
public sealed class ProfileApiTests(SignInSiteFixture site, PostgresFixture postgres) : IClassFixture<SignInSiteFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Profile_SignedIn_ReturnsStats()
    {
        const long Owner = 650_001L;
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, Owner, Ct);
        using (HttpResponseMessage batch = await PostBatchAsync(client, Owner, Ct, Answer(ClosureQuestionId), Answer(ConfigureAwaitQuestionId)))
        {
            Assert.Equal(HttpStatusCode.OK, batch.StatusCode);
        }

        using HttpResponseMessage response = await client.GetAsync("/api/profile?culture=en&stack=dotnet", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore, "Every /api response is no-store.");
        ProfileDto profile = await ReadAsync(response, TrainerJsonContext.Default.ProfileDto, Ct);
        Assert.Equal(2, profile.TotalAnswered);
        Assert.Equal(2, profile.Topics.Sum(t => t.Answered));
    }

    [Theory]
    [InlineData("/api/profile?culture=xx&stack=dotnet")]
    [InlineData("/api/profile?culture=en&stack=no-such-stack")]
    [InlineData("/api/profile")]
    public async Task Profile_InvalidQuery_Returns400(string path)
    {
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, 650_002L, Ct);

        using HttpResponseMessage response = await client.GetAsync(path, Ct);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, ApiErrorCodes.Validation, Ct);
    }

    [Fact]
    public async Task ApiUnauthenticated_Returns401WithoutLocation()
    {
        using HttpClient client = SignInTestHelpers.Browser(site.Factory);

        using HttpResponseMessage response = await client.GetAsync("/api/profile?culture=en&stack=dotnet", Ct);

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, ApiErrorCodes.SignInRequired, Ct);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task BlockedUser_ApiReturns401AfterInterval()
    {
        const long Owner = 650_003L;
        await using SessionSite session = await SessionSite.StartAsync(postgres);
        (HttpClient client, HttpResponseMessage callback) = await session.SignInAsync(new FakeTelegramIdentity(Owner, "Blocked Person", null), Ct);
        using HttpClient disposableClient = client;
        callback.Dispose();
        using (HttpResponseMessage before = await client.GetAsync("/api/profile?culture=en&stack=dotnet", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        UserAccess access = await session.AccessOfAsync(Owner, Ct);
        await session.WithUsersAsync(users => users.SetBlockedAsync(access.UserId, blocked: true, Ct));
        session.Clock.Advance(TimeSpan.FromMinutes(6));

        using HttpResponseMessage response = await client.GetAsync("/api/profile?culture=en&stack=dotnet", Ct);

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, ApiErrorCodes.SignInRequired, Ct);
        Assert.Null(response.Headers.Location);
    }

    [Theory]
    [InlineData("680011", HttpStatusCode.OK)]
    [InlineData("680012", HttpStatusCode.Conflict)]
    [InlineData("anon", HttpStatusCode.Conflict)]
    public async Task Profile_ChecksTheTabsOwner(string owner, HttpStatusCode expected)
    {
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, 680_011L, Ct);
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/profile?culture=en&stack=dotnet");
        request.Headers.TryAddWithoutValidation(OwnerHeader, owner);

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        Assert.Equal(expected, response.StatusCode);
    }
}
