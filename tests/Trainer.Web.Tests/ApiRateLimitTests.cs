using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Trainer.Contracts;
using Trainer.TestSupport;
using Trainer.Web.Api;
using Trainer.Web.Auth;
using static Trainer.Web.Tests.ApiTestHelpers;

namespace Trainer.Web.Tests;

/// <summary>A site with three writes and three reads per minute, whose requests carry no remote address.</summary>
public sealed class LowRateLimitSiteFixture(PostgresFixture postgres) : IAsyncLifetime
{
    public const int Limit = 3;

    public FakeTelegramOidcServer Telegram { get; private set; } = null!;

    public TrainerWebFactory Factory { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Telegram = await FakeTelegramOidcServer.StartAsync(TrainerWebFactoryOptions.TelegramClientId);
        Factory = await TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions
        {
            Telegram = Telegram,
            Settings = new Dictionary<string, string?>
            {
                ["App:RateLimits:WritesPerMinute"] = "3",
                ["App:RateLimits:ReadsPerMinute"] = "3",
            },
            ConfigureServices = services => services.AddSingleton<IStartupFilter, NoRemoteAddress>(),
        });
        await Factory.WaitUntilReadyAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Telegram.DisposeAsync();
    }

    /// <summary>Makes the missing address explicit rather than relying on what the test server happens to set.</summary>
    private sealed class NoRemoteAddress : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = null;
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}

/// <summary>Per-user write limits, per-user-or-address read limits (<c>App:RateLimits</c>).</summary>
public sealed class ApiRateLimitTests(LowRateLimitSiteFixture site, PostgresFixture postgres) : IClassFixture<LowRateLimitSiteFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StringContent PracticeBody() => Json(Practice(count: 1), TrainerJsonContext.Default.PracticeSessionRequest);

    private static async Task<HttpResponseMessage> ReadAsync(HttpClient client) =>
        await client.GetAsync("/api/profile?culture=en&stack=dotnet", Ct);

    [Fact]
    public async Task Writes_RateLimitedPerUser()
    {
        const long Alice = 670_001L;
        const long Bob = 670_002L;
        using HttpClient alice = await SignInAsync(site.Factory, site.Telegram, Alice, Ct);
        using HttpClient bob = await SignInAsync(site.Factory, site.Telegram, Bob, Ct);

        for (int i = 0; i < LowRateLimitSiteFixture.Limit; i++)
        {
            using HttpResponseMessage allowed = await PostBatchAsync(alice, Alice, Ct, Answer(ClosureQuestionId));
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using HttpResponseMessage limited = await PostBatchAsync(alice, Alice, Ct, Answer(ClosureQuestionId));
        using HttpResponseMessage other = await PostBatchAsync(bob, Bob, Ct, Answer(ClosureQuestionId));

        await AssertErrorAsync(limited, HttpStatusCode.TooManyRequests, ApiErrorCodes.RateLimited, Ct);
        Assert.NotNull(limited.Headers.RetryAfter?.Delta);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task Reads_PartitionByUserWhenSignedIn()
    {
        using HttpClient carol = await SignInAsync(site.Factory, site.Telegram, 670_003L, Ct);
        using HttpClient dave = await SignInAsync(site.Factory, site.Telegram, 670_004L, Ct);

        for (int i = 0; i < LowRateLimitSiteFixture.Limit; i++)
        {
            using HttpResponseMessage allowed = await ReadAsync(carol);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using HttpResponseMessage limited = await ReadAsync(carol);
        using HttpResponseMessage other = await ReadAsync(dave);

        await AssertErrorAsync(limited, HttpStatusCode.TooManyRequests, ApiErrorCodes.RateLimited, Ct);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task Reads_NullRemoteAddress_UsesFallbackPartition()
    {
        using HttpClient first = SignInTestHelpers.Browser(site.Factory);
        using HttpClient second = SignInTestHelpers.Browser(site.Factory);
        using HttpClient signedIn = await SignInAsync(site.Factory, site.Telegram, 670_005L, Ct);

        for (int i = 0; i < LowRateLimitSiteFixture.Limit; i++)
        {
            using HttpResponseMessage allowed = await first.PostAsync("/api/sessions/practice", PracticeBody(), Ct);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        // A different anonymous client without an address shares the fallback partition; a signed-in user does not.
        using HttpResponseMessage limited = await second.PostAsync("/api/sessions/practice", PracticeBody(), Ct);
        using HttpResponseMessage user = await signedIn.PostAsync("/api/sessions/practice", PracticeBody(), Ct);

        await AssertErrorAsync(limited, HttpStatusCode.TooManyRequests, ApiErrorCodes.RateLimited, Ct);
        Assert.Equal(HttpStatusCode.OK, user.StatusCode);
    }

    [Fact]
    public async Task Reads_NonJsonPosts_AreRefusedWithoutSpendingTheBudget()
    {
        using HttpClient eve = await SignInAsync(site.Factory, site.Telegram, 670_006L, Ct);

        // A cross-site page can send text/plain without a preflight; such a request must not use up the read budget.
        for (int i = 0; i < LowRateLimitSiteFixture.Limit * 2; i++)
        {
            using StringContent plain = new("{}", System.Text.Encoding.UTF8, "text/plain");
            using HttpResponseMessage refused = await eve.PostAsync("/api/sessions/practice", plain, Ct);
            await AssertErrorAsync(refused, HttpStatusCode.UnsupportedMediaType, ApiErrorCodes.UnsupportedMediaType, Ct);
        }

        for (int i = 0; i < LowRateLimitSiteFixture.Limit; i++)
        {
            using HttpResponseMessage allowed = await eve.PostAsync("/api/sessions/practice", PracticeBody(), Ct);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using HttpResponseMessage limited = await eve.PostAsync("/api/sessions/practice", PracticeBody(), Ct);
        await AssertErrorAsync(limited, HttpStatusCode.TooManyRequests, ApiErrorCodes.RateLimited, Ct);
    }

    [Fact]
    public void ReadPartition_PrefersUserThenAddressThenFallback()
    {
        DefaultHttpContext anonymous = new();
        DefaultHttpContext withAddress = new();
        withAddress.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        DefaultHttpContext signedIn = new();
        signedIn.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        signedIn.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(TrainerClaimTypes.UserId, "01KC0000000000000000000009")], "test"));

        Assert.Equal(ApiRateLimits.FallbackPartition, ApiRateLimits.ReadPartition(anonymous));
        Assert.Equal("ip:203.0.113.7", ApiRateLimits.ReadPartition(withAddress));
        Assert.Equal("user:01KC0000000000000000000009", ApiRateLimits.ReadPartition(signedIn));
    }

    [Theory]
    [InlineData("App:RateLimits:WritesPerMinute", "0")]
    [InlineData("App:RateLimits:ReadsPerMinute", "-1")]
    [InlineData("App:RateLimits:SearchPerMinute", "0")]
    public void RateLimits_NonPositive_Throw(string key, string value)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>(key, value)])
            .Build();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => ApiRateLimits.Read(configuration));

        Assert.Contains(key, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_NonPositiveRateLimit_Throws()
    {
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions
        {
            Settings = new Dictionary<string, string?> { ["App:RateLimits:ReadsPerMinute"] = "0" },
        });

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => factory.Services);

        Assert.Contains("App:RateLimits:ReadsPerMinute", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestHosts_UseRaisedLimits()
    {
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres);

        RateLimitOptions limits = factory.Services.GetRequiredService<IOptions<RateLimitOptions>>().Value;

        Assert.Equal(100_000, limits.WritesPerMinute);
        Assert.Equal(100_000, limits.ReadsPerMinute);
        Assert.Equal(100_000, limits.SearchPerMinute);
    }
}
