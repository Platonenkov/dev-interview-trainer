using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Trainer.Contracts;

namespace Trainer.Web.Client.Api;

/// <summary>
/// The islands' only way to the server's <c>/api</c> (spec 8.8). Bodies go through <see cref="TrainerJsonContext"/>;
/// every response's <c>Date</c> calibrates the <see cref="ServerClock"/>. Writes carry the antiforgery token, the outbox
/// owner (ADR 0008) and the device's UTC offset; the owner is a parameter, so it stays the same for the antiforgery
/// refetch-and-retry even if another tab switches accounts meanwhile; cancelling the write's token (a stopped sync) skips
/// that retry. Reads carry the page's owner once <see cref="BindOwner"/> was called, so the server refuses them with 409
/// when the cookie now belongs to someone else. Nothing here throws for HTTP or network failures: they come back as an
/// <see cref="ApiResult{T}"/>.
/// </summary>
public sealed class TrainerApiClient(HttpClient http, AntiforgeryTokenProvider tokens, ServerClock clock, TimeProvider device)
{
    public const string TokenHeader = "X-CSRF-TOKEN";
    public const string OwnerHeader = "X-Outbox-Owner";
    public const string UtcOffsetHeader = "X-Client-Utc-Offset";

    public const string AnonymousOwner = "anon";

    private string? _readOwner;

    /// <summary>
    /// Binds the account the page was rendered for (a Telegram user id, or <see cref="AnonymousOwner"/>); every later read
    /// names it in <see cref="OwnerHeader"/>. Binding the same owner again does nothing.
    /// </summary>
    /// <exception cref="ArgumentException">Neither a positive decimal id nor <see cref="AnonymousOwner"/>.</exception>
    /// <exception cref="InvalidOperationException">Another owner is already bound.</exception>
    public void BindOwner(string owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        bool valid = string.Equals(owner, AnonymousOwner, StringComparison.Ordinal)
            || (long.TryParse(owner, NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 0);
        if (!valid)
        {
            throw new ArgumentException("The owner is neither a Telegram user id nor anon.", nameof(owner));
        }

        if (_readOwner is not null && !string.Equals(_readOwner, owner, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The API client is already bound to another owner.");
        }

        _readOwner = owner;
    }

    public Task<ApiResult<SessionDto>> PracticeAsync(PracticeSessionRequest request, CancellationToken ct) =>
        SendAsync(() => Read(Post("api/sessions/practice", request, TrainerJsonContext.Default.PracticeSessionRequest)), TrainerJsonContext.Default.SessionDto, ct);

    public Task<ApiResult<MockSessionDto>> MockAsync(MockSessionRequest request, CancellationToken ct) =>
        SendAsync(() => Read(Post("api/sessions/mock", request, TrainerJsonContext.Default.MockSessionRequest)), TrainerJsonContext.Default.MockSessionDto, ct);

    public Task<ApiResult<SessionDto>> ReviewAsync(ReviewSessionRequest request, CancellationToken ct) =>
        SendAsync(() => Read(Post("api/sessions/review", request, TrainerJsonContext.Default.ReviewSessionRequest)), TrainerJsonContext.Default.SessionDto, ct);

    public Task<ApiResult<ProfileDto>> ProfileAsync(string culture, string stack, CancellationToken ct) =>
        SendAsync(
            () => Read(new HttpRequestMessage(HttpMethod.Get, $"api/profile?culture={Uri.EscapeDataString(culture)}&stack={Uri.EscapeDataString(stack)}")),
            TrainerJsonContext.Default.ProfileDto,
            ct);

    /// <summary><c>POST /api/answers/batch</c> for the outbox of <paramref name="owner"/> (a Telegram user id).</summary>
    public Task<ApiResult<AnswerBatchResponse>> PostAnswersAsync(long owner, AnswerBatch batch, CancellationToken ct) =>
        SendWriteAsync(
            owner,
            () => Post("api/answers/batch", batch, TrainerJsonContext.Default.AnswerBatch),
            TrainerJsonContext.Default.AnswerBatchResponse,
            ct);

    /// <summary><c>PUT /api/mock-sessions/{id}</c>; <see cref="ApiResult{T}.Value"/> is true when the server stored it (204).</summary>
    public async Task<ApiResult<bool>> PutMockSummaryAsync(long owner, string mockSessionId, MockSummaryDto summary, CancellationToken ct)
    {
        ApiResult<MockSummaryDto> result = await SendWriteAsync<MockSummaryDto>(
            owner,
            () => Json(HttpMethod.Put, "api/mock-sessions/" + Uri.EscapeDataString(mockSessionId), summary, TrainerJsonContext.Default.MockSummaryDto),
            null,
            ct);
        // Only the endpoint's own 204 says the summary is stored: a 200 can be a captive portal's or a proxy's page.
        return new ApiResult<bool>(result.Status == (int)HttpStatusCode.NoContent, result.Status, result.ErrorCode);
    }

    /// <summary>
    /// <c>PUT /api/questions/{id}/rating</c> for <paramref name="value"/> (<c>like</c> or <c>dislike</c>), or
    /// <c>DELETE</c> when it is null, which clears the rating.
    /// </summary>
    public Task<ApiResult<RatingDto>> SetRatingAsync(long owner, string questionId, string? value, CancellationToken ct)
    {
        string path = "api/questions/" + Uri.EscapeDataString(questionId) + "/rating";
        return SendWriteAsync(
            owner,
            () => value is null
                ? new HttpRequestMessage(HttpMethod.Delete, path)
                : Json(HttpMethod.Put, path, new RatingRequest(value), TrainerJsonContext.Default.RatingRequest),
            TrainerJsonContext.Default.RatingDto,
            ct);
    }

    /// <summary><c>POST /api/questions/{id}/reports</c>: 201 is a new report, 200 an edited open one, 429 <c>report-limit</c> the daily limit.</summary>
    public Task<ApiResult<ReportResponse>> ReportAsync(long owner, string questionId, ReportRequest request, CancellationToken ct) =>
        SendWriteAsync(
            owner,
            () => Post("api/questions/" + Uri.EscapeDataString(questionId) + "/reports", request, TrainerJsonContext.Default.ReportRequest),
            TrainerJsonContext.Default.ReportResponse,
            ct);

    private async Task<ApiResult<T>> SendWriteAsync<T>(
        long owner,
        Func<HttpRequestMessage> build,
        JsonTypeInfo<T>? responseType,
        CancellationToken ct)
    {
        ApiResult<T> result = await SendWithTokenAsync(owner, build, responseType, ct);
        if (result.Status == (int)HttpStatusCode.BadRequest
            && string.Equals(result.ErrorCode, ApiErrorCodes.Antiforgery, StringComparison.Ordinal)
            && !ct.IsCancellationRequested)
        {
            // The token belongs to whoever was signed in when it was issued; fetch a fresh one once.
            tokens.Invalidate();
            result = await SendWithTokenAsync(owner, build, responseType, ct);
        }

        return result;
    }

    private async Task<ApiResult<T>> SendWithTokenAsync<T>(
        long owner,
        Func<HttpRequestMessage> build,
        JsonTypeInfo<T>? responseType,
        CancellationToken ct)
    {
        string token;
        try
        {
            token = await tokens.GetAsync(ct);
        }
        catch (HttpRequestException exception)
        {
            // The write was never sent. An error status is passed on (401, 429 and 5xx mean the same for the write); a
            // 2xx without a token (a captive portal's page) must never look like the write's success.
            int status = exception.StatusCode is { } code && (int)code >= 300 ? (int)code : 0;
            return new ApiResult<T>(default, status, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return NoResponse<T>();
        }

        return await SendAsync(
            () =>
            {
                HttpRequestMessage request = build();
                request.Headers.TryAddWithoutValidation(TokenHeader, token);
                request.Headers.TryAddWithoutValidation(OwnerHeader, owner.ToString(CultureInfo.InvariantCulture));
                int offsetMinutes = (int)device.GetLocalNow().Offset.TotalMinutes;
                request.Headers.TryAddWithoutValidation(UtcOffsetHeader, offsetMinutes.ToString(CultureInfo.InvariantCulture));
                return request;
            },
            responseType,
            ct);
    }

    private async Task<ApiResult<T>> SendAsync<T>(Func<HttpRequestMessage> build, JsonTypeInfo<T>? responseType, CancellationToken ct)
    {
        using HttpRequestMessage request = build();
        DateTimeOffset sentAt = device.GetUtcNow();
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            return NoResponse<T>();
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient's own timeout.
            return NoResponse<T>();
        }

        using (response)
        {
            DateTimeOffset receivedAt = device.GetUtcNow();
            if (response.Headers.Date is { } serverDate)
            {
                clock.Observe(serverDate, sentAt, receivedAt);
            }

            int status = (int)response.StatusCode;
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(ct);
            }
            catch (HttpRequestException)
            {
                // The connection broke while the body was streaming: the status alone is not an acknowledgement.
                return NoResponse<T>();
            }
            if (response.IsSuccessStatusCode)
            {
                return new ApiResult<T>(responseType is null ? default : Deserialize(body, responseType), status, null);
            }

            return new ApiResult<T>(default, status, Deserialize(body, TrainerJsonContext.Default.ApiErrorDto)?.Code, RetryAfter(response, receivedAt));
        }
    }

    private static ApiResult<T> NoResponse<T>() => new(default, 0, null);

    // An HTTP-date Retry-After is measured against the response's own Date, so a skewed device clock cannot distort it.
    private static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset receivedAt) => response.Headers.RetryAfter switch
    {
        { Delta: { } delta } => delta,
        { Date: { } date } when date - (response.Headers.Date ?? receivedAt) is { } wait => wait > TimeSpan.Zero ? wait : TimeSpan.Zero,
        _ => null,
    };

    private static T? Deserialize<T>(string body, JsonTypeInfo<T> type)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize(body, type);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private HttpRequestMessage Read(HttpRequestMessage request)
    {
        if (_readOwner is { } owner)
        {
            request.Headers.TryAddWithoutValidation(OwnerHeader, owner);
        }

        return request;
    }

    private static HttpRequestMessage Post<TBody>(string path, TBody body, JsonTypeInfo<TBody> type) => Json(HttpMethod.Post, path, body, type);

    private static HttpRequestMessage Json<TBody>(HttpMethod method, string path, TBody body, JsonTypeInfo<TBody> type) =>
        new(method, path) { Content = new StringContent(JsonSerializer.Serialize(body, type), Encoding.UTF8, "application/json") };
}
