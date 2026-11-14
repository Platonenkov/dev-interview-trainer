using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Trainer.Contracts;
using Trainer.Web.Client.Api;

namespace Trainer.Web.Client.Tests;

public sealed class TrainerApiClientTests
{
    private static readonly DateTimeOffset DeviceStart = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (TrainerApiClient Api, FakeHttpHandler Handler, ServerClock Clock) Create(FakeTimeProvider? device = null)
    {
        device ??= new FakeTimeProvider(DeviceStart);
        FakeHttpHandler handler = new();
        HttpClient http = handler.Client();
        ServerClock clock = new(device, new FakeBrowserStore());
        return (new TrainerApiClient(http, new AntiforgeryTokenProvider(http), clock, device), handler, clock);
    }

    private static ReviewSessionRequest Review => new("en", "dotnet", 10);

    [Fact]
    public async Task Api_NonJsonErrorBody_HasNoCode()
    {
        (TrainerApiClient api, FakeHttpHandler handler, _) = Create();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html><body>Bad gateway</body></html>", Encoding.UTF8, "text/html"),
        };

        ApiResult<SessionDto> result = await api.ReviewAsync(Review, Ct);

        Assert.Equal(502, result.Status);
        Assert.Null(result.ErrorCode);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Api_JsonErrorBody_HasItsCode()
    {
        (TrainerApiClient api, FakeHttpHandler handler, _) = Create();
        handler.Respond = _ => FakeHttpHandler.Error(HttpStatusCode.Unauthorized, ApiErrorCodes.SignInRequired);

        ApiResult<SessionDto> result = await api.ReviewAsync(Review, Ct);

        Assert.Equal(401, result.Status);
        Assert.Equal(ApiErrorCodes.SignInRequired, result.ErrorCode);
    }

    [Fact]
    public async Task Api_429_CarriesRetryAfter()
    {
        (TrainerApiClient api, FakeHttpHandler handler, _) = Create();
        handler.Respond = _ =>
        {
            HttpResponseMessage response = FakeHttpHandler.Error(HttpStatusCode.TooManyRequests, ApiErrorCodes.RateLimited);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(17));
            return response;
        };

        ApiResult<SessionDto> limited = await api.ReviewAsync(Review, Ct);
        handler.Respond = _ => FakeHttpHandler.Error(HttpStatusCode.TooManyRequests, ApiErrorCodes.RateLimited);
        ApiResult<SessionDto> withoutHeader = await api.ReviewAsync(Review, Ct);

        Assert.Equal(429, limited.Status);
        Assert.Equal(TimeSpan.FromSeconds(17), limited.RetryAfter);
        Assert.Null(withoutHeader.RetryAfter);
    }

    [Fact]
    public async Task Api_429_RetryAfterDate_IsMeasuredFromTheResponseDate()
    {
        // The device clock is an hour behind the server: only the response's own Date gives the right wait.
        (TrainerApiClient api, FakeHttpHandler handler, _) = Create();
        DateTimeOffset serverNow = DeviceStart.AddHours(1);
        handler.Respond = _ => RateLimited(serverNow, serverNow.AddSeconds(30));
        ApiResult<SessionDto> future = await api.ReviewAsync(Review, Ct);

        handler.Respond = _ => RateLimited(serverNow, serverNow.AddSeconds(-30));
        ApiResult<SessionDto> past = await api.ReviewAsync(Review, Ct);

        Assert.Equal(TimeSpan.FromSeconds(30), future.RetryAfter);
        Assert.Equal(TimeSpan.Zero, past.RetryAfter);

        static HttpResponseMessage RateLimited(DateTimeOffset date, DateTimeOffset retryAt)
        {
            HttpResponseMessage response = FakeHttpHandler.Error(HttpStatusCode.TooManyRequests, ApiErrorCodes.RateLimited);
            response.Headers.Date = date;
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAt);
            return response;
        }
    }

    [Fact]
    public async Task Api_NetworkFailure_HasStatusZero()
    {
        (TrainerApiClient api, FakeHttpHandler handler, _) = Create();
        handler.Respond = _ => throw new HttpRequestException("offline");

        ApiResult<SessionDto> read = await api.ReviewAsync(Review, Ct);
        ApiResult<AnswerBatchResponse> write = await api.PostAnswersAsync(42, new AnswerBatch(1, []), Ct);

        Assert.Equal(0, read.Status);
        Assert.Equal(0, write.Status);
        Assert.Null(read.ErrorCode);
    }

    [Fact]
    public async Task Api_Success_ReadsBodyAndCalibratesClockFromDate()
    {
        FakeTimeProvider device = new(DeviceStart);
        (TrainerApiClient api, FakeHttpHandler handler, ServerClock clock) = Create(device);
        handler.Date = DeviceStart.AddMinutes(10);
        handler.Respond = r => r.Path == "/api/sessions/review"
            ? FakeHttpHandler.Json(HttpStatusCode.OK, "{\"questions\":[],\"poolSize\":3}")
            : null;

        ApiResult<SessionDto> result = await api.ReviewAsync(Review, Ct);

        Assert.Equal(200, result.Status);
        Assert.Equal(3, result.Value!.PoolSize);
        Assert.Equal(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(0.5), clock.Offset);
        RecordedRequest sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Contains("\"stack\":\"dotnet\"", sent.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_PutSummary_SendsWriteHeadersAndReports204()
    {
        (TrainerApiClient api, FakeHttpHandler handler, _) = Create();
        handler.Respond = r => r.Method == HttpMethod.Put ? new HttpResponseMessage(HttpStatusCode.NoContent) : null;
        MockSummaryDto summary = new("dotnet", "middle", 1, false, DeviceStart, DeviceStart.AddMinutes(5), [new MockTopicResultDto("async", 1, 0, 0, 0)]);

        ApiResult<bool> result = await api.PutMockSummaryAsync(42, "01KC0000000000000000000001", summary, Ct);

        Assert.Equal(204, result.Status);
        Assert.True(result.Value);
        RecordedRequest put = Assert.Single(handler.Writes);
        Assert.Equal("/api/mock-sessions/01KC0000000000000000000001", put.Path);
        Assert.Equal("token-1", put.Header("X-CSRF-TOKEN"));
        Assert.Equal("42", put.Header("X-Outbox-Owner"));
        Assert.Equal("0", put.Header("X-Client-Utc-Offset"));
    }

    [Fact]
    public async Task Api_Profile_QueriesCultureAndStack()
    {
        (TrainerApiClient api, FakeHttpHandler handler, _) = Create();

        ApiResult<ProfileDto> result = await api.ProfileAsync("ru", "dotnet", Ct);

        Assert.Equal(404, result.Status);
        Assert.Equal("/api/profile?culture=ru&stack=dotnet", Assert.Single(handler.Requests).Path);
    }

    [Fact]
    public async Task Api_PutSummary_2xxOtherThan204_IsNotStored()
    {
        (TrainerApiClient api, FakeHttpHandler handler, _) = Create();
        handler.Respond = r => r.Method == HttpMethod.Put
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Wi-Fi login</html>", Encoding.UTF8, "text/html") }
            : null;

        ApiResult<bool> result = await api.PutMockSummaryAsync(42, "01KC0000000000000000000001", Summary(), Ct);

        Assert.Equal(200, result.Status);
        Assert.False(result.Value);
    }

    [Theory]
    [InlineData("<html>Wi-Fi login</html>", "text/html")]
    [InlineData("", "text/plain")]
    [InlineData("{\"requestToken\":\"\",\"headerName\":\"X-CSRF-TOKEN\"}", "application/json")]
    public async Task Api_TokenFetchWith2xxButNoToken_IsLikeNoResponse(string body, string mediaType)
    {
        (TrainerApiClient api, FakeHttpHandler handler, _) = Create();
        handler.Respond = r => r.Path == FakeHttpHandler.AntiforgeryPath
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) }
            : new HttpResponseMessage(HttpStatusCode.NoContent);

        ApiResult<bool> put = await api.PutMockSummaryAsync(42, "01KC0000000000000000000001", Summary(), Ct);
        ApiResult<AnswerBatchResponse> post = await api.PostAnswersAsync(42, new AnswerBatch(1, []), Ct);

        Assert.Equal(0, put.Status);
        Assert.False(put.Value);
        Assert.Equal(0, post.Status);
        Assert.Null(post.Value);
        Assert.Empty(handler.Writes);
    }

    [Fact]
    public async Task Api_TokenFetchWithError_KeepsItsStatus()
    {
        (TrainerApiClient api, FakeHttpHandler handler, _) = Create();
        handler.Respond = r => r.Path == FakeHttpHandler.AntiforgeryPath
            ? FakeHttpHandler.Error(HttpStatusCode.TooManyRequests, ApiErrorCodes.RateLimited)
            : null;

        ApiResult<bool> put = await api.PutMockSummaryAsync(42, "01KC0000000000000000000001", Summary(), Ct);

        Assert.Equal(429, put.Status);
        Assert.False(put.Value);
        Assert.Empty(handler.Writes);
    }

    private static MockSummaryDto Summary() =>
        new("dotnet", "middle", 1, false, DeviceStart, DeviceStart.AddMinutes(5), [new MockTopicResultDto("async", 1, 0, 0, 0)]);

    [Fact]
    public async Task Api_Reads_CarryTheBoundOwner()
    {
        (TrainerApiClient api, FakeHttpHandler handler, _) = Create();
        await api.ReviewAsync(Review, Ct);

        api.BindOwner("anon");
        await api.ProfileAsync("en", "dotnet", Ct);
        await api.PracticeAsync(new PracticeSessionRequest("en", "dotnet", [], [], [], "all", 5, null, []), Ct);
        await api.MockAsync(new MockSessionRequest("en", "dotnet", "middle", [], 5), Ct);

        Assert.Null(handler.Requests[0].Header("X-Outbox-Owner"));
        Assert.All(handler.Requests.Skip(1), r => Assert.Equal("anon", r.Header("X-Outbox-Owner")));
    }

    [Fact]
    public void Api_BindOwner_OnceOnly()
    {
        (TrainerApiClient api, _, _) = Create();
        api.BindOwner("42");
        api.BindOwner("42");

        Assert.Throws<InvalidOperationException>(() => api.BindOwner("7"));
        Assert.Throws<ArgumentException>(() => api.BindOwner("4x"));
    }
}
