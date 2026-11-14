using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Trainer.Application.Sessions;
using Trainer.Contracts;
using Trainer.Domain.Users;
using Trainer.TestSupport;
using static Trainer.Web.Tests.ApiTestHelpers;

namespace Trainer.Web.Tests;

/// <summary>
/// A site whose write limit is one per minute, so a second write is 429, and where a request marked with
/// <see cref="ForbidHeader"/> fails authorization, so a signed-in caller gets the 403 no real endpoint produces yet.
/// </summary>
public sealed class ApiErrorSiteFixture(PostgresFixture postgres) : IAsyncLifetime
{
    public const string ForbidHeader = "X-Test-Forbid";

    public FakeTelegramOidcServer Telegram { get; private set; } = null!;

    public TrainerWebFactory Factory { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Telegram = await FakeTelegramOidcServer.StartAsync(TrainerWebFactoryOptions.TelegramClientId);
        Factory = await TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions
        {
            Telegram = Telegram,
            Settings = new Dictionary<string, string?> { ["App:RateLimits:WritesPerMinute"] = "1" },
            ConfigureServices = services => services.AddSingleton<IAuthorizationHandler, ForbidMarkedRequests>(),
        });
        await Factory.WaitUntilReadyAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Telegram.DisposeAsync();
    }

    private sealed class ForbidMarkedRequests : IAuthorizationHandler
    {
        public Task HandleAsync(AuthorizationHandlerContext context)
        {
            if (context.Resource is HttpContext http && http.Request.Headers.ContainsKey(ForbidHeader))
            {
                context.Fail();
            }

            return Task.CompletedTask;
        }
    }
}

/// <summary>Every non-success <c>/api</c> response is an <see cref="ApiErrorDto"/>, never the HTML not-found page.</summary>
public sealed class ApiErrorTests(ApiErrorSiteFixture site, PostgresFixture postgres) : IClassFixture<ApiErrorSiteFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<int> ErrorStatuses() => [400, 401, 403, 404, 405, 413, 415, 429];

    [Theory]
    [MemberData(nameof(ErrorStatuses))]
    public async Task ApiErrors_AreJsonNotHtml(int status)
    {
        using HttpResponseMessage response = await ProvokeAsync(status);

        string code = status switch
        {
            400 => ApiErrorCodes.Validation,
            401 => ApiErrorCodes.SignInRequired,
            403 => ApiErrorCodes.Forbidden,
            404 => ApiErrorCodes.NotFound,
            405 => ApiErrorCodes.MethodNotAllowed,
            413 => ApiErrorCodes.PayloadTooLarge,
            415 => ApiErrorCodes.UnsupportedMediaType,
            _ => ApiErrorCodes.RateLimited,
        };
        await AssertErrorAsync(response, (HttpStatusCode)status, code, Ct);
        Assert.True(response.Headers.CacheControl?.NoStore, "Every /api response is no-store.");
        if (status == 429)
        {
            Assert.NotNull(response.Headers.RetryAfter);
        }
    }

    private async Task<HttpResponseMessage> ProvokeAsync(int status)
    {
        // The responses are buffered (PostAsync and GetAsync read the content), so the clients can go with this method.
        using HttpClient anonymous = SignInTestHelpers.Browser(site.Factory);
        switch (status)
        {
            case 400:
                return await anonymous.PostAsync("/api/sessions/practice", Json("""{"culture":"""), Ct);
            case 401:
                return await anonymous.PostAsync(
                    "/api/sessions/review", Json(new ReviewSessionRequest("en", "dotnet", 5), TrainerJsonContext.Default.ReviewSessionRequest), Ct);
            case 403:
                {
                    using HttpClient client = await SignInAsync(site.Factory, site.Telegram, 660_001L, Ct);
                    using HttpRequestMessage request = new(HttpMethod.Get, "/api/profile?culture=en&stack=dotnet");
                    request.Headers.TryAddWithoutValidation(ApiErrorSiteFixture.ForbidHeader, "1");
                    return await client.SendAsync(request, Ct);
                }

            case 404:
                return await anonymous.GetAsync("/api/x", Ct);
            case 405:
                // DELETE, not GET: the static asset fallback route accepts any GET, so a GET to a POST-only path is 404.
                return await anonymous.DeleteAsync("/api/answers/batch", Ct);
            case 413:
                return await anonymous.PostAsync("/api/sessions/practice", Json($$"""{"padding":"{{new string('a', 300 * 1024)}}"}"""), Ct);
            case 415:
                return await anonymous.PostAsync("/api/sessions/practice", new StringContent("{}", Encoding.UTF8, "text/plain"), Ct);
            default:
                {
                    const long Owner = 660_002L;
                    using HttpClient client = await SignInAsync(site.Factory, site.Telegram, Owner, Ct);
                    using HttpResponseMessage first = await PostBatchAsync(client, Owner, Ct, Answer(ClosureQuestionId));
                    Assert.Equal(HttpStatusCode.OK, first.StatusCode);
                    return await PostBatchAsync(client, Owner, Ct, Answer(ClosureQuestionId));
                }
        }
    }

    [Fact]
    public async Task ApiUnhandledException_Logged_And500Json()
    {
        CapturingLoggerProvider logs = new();
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions
        {
            Logs = logs,
            ConfigureServices = services => services.Replace(ServiceDescriptor.Scoped<ISessionQueries, ThrowingSessionQueries>()),
        });
        await factory.WaitUntilReadyAsync();
        using HttpClient client = SignInTestHelpers.Browser(factory);

        using HttpResponseMessage response = await client.PostAsync(
            "/api/sessions/practice", Json(Practice(), TrainerJsonContext.Default.PracticeSessionRequest), Ct);

        await AssertErrorAsync(response, HttpStatusCode.InternalServerError, ApiErrorCodes.ServerError, Ct);
        string body = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(ThrowingSessionQueries.Message, body, StringComparison.Ordinal);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Error && e.Exception?.Contains(ThrowingSessionQueries.Message, StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task NoInteractiveServerEndpoints()
    {
        using HttpClient client = SignInTestHelpers.Browser(site.Factory);

        using HttpResponseMessage response = await client.PostAsync("/_blazor/negotiate", new StringContent(""), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("id=\"not-found\"", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    private sealed class ThrowingSessionQueries : ISessionQueries
    {
        public const string Message = "secret detail of a failing query";

        public Task<SessionDto> GetPracticeAsync(PracticeCriteria criteria, UserId? user, CancellationToken ct) =>
            throw new InvalidOperationException(Message);

        public Task<MockSessionDto> GetMockAsync(MockCriteria criteria, CancellationToken ct) =>
            throw new InvalidOperationException(Message);

        public Task<SessionDto> GetReviewAsync(ReviewCriteria criteria, UserId user, CancellationToken ct) =>
            throw new InvalidOperationException(Message);
    }
}
