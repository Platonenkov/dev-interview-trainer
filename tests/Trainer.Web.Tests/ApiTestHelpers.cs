using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Trainer.Application.Users;
using Trainer.Contracts;
using Trainer.Domain.Ids;
using Trainer.Infrastructure.Persistence;
using Trainer.TestSupport;
using Trainer.Web.Auth;
using Trainer.Web.Caching;

namespace Trainer.Web.Tests;

/// <summary>Requests and assertions shared by the <c>/api</c> tests.</summary>
internal static class ApiTestHelpers
{
    /// <summary>The csharp question <c>closure-for-loop</c> (junior) of the web fixture.</summary>
    public const string ClosureQuestionId = "01KBZ3Q8N9V2K3M4P5R6S7T8WA";

    /// <summary>The async question <c>configureawait-false-purpose</c> (middle) of the web fixture.</summary>
    public const string ConfigureAwaitQuestionId = "01KBZ3Q8N9V2K3M4P5R6S7T8WB";

    public const string OwnerHeader = "X-Outbox-Owner";
    public const string OffsetHeader = "X-Client-Utc-Offset";
    public const string TokenHeader = "X-CSRF-TOKEN";

    private static readonly UlidGenerator Ulids = new(TimeProvider.System);

    public static string NewUlid() => Ulids.Next();

    /// <summary>A cookie-keeping, non-redirecting client signed in as <paramref name="telegramId"/>.</summary>
    public static async Task<HttpClient> SignInAsync(TrainerWebFactory factory, FakeTelegramOidcServer telegram, long telegramId, CancellationToken ct)
    {
        HttpClient client = SignInTestHelpers.Browser(factory);
        using HttpResponseMessage callback = await TelegramSignInDriver.SignInAsync(
            client, telegram, new FakeTelegramIdentity(telegramId, "Api Person " + telegramId, null), "/en", ct);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        return client;
    }

    /// <summary>Fetches a token from <c>GET /api/antiforgery</c>; the client keeps the antiforgery cookie.</summary>
    public static async Task<string> GetTokenAsync(HttpClient client, CancellationToken ct)
    {
        using HttpResponseMessage response = await client.GetAsync("/api/antiforgery", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AntiforgeryTokenDto token = await ReadAsync(response, TrainerJsonContext.Default.AntiforgeryTokenDto, ct);
        return token.RequestToken;
    }

    public static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    public static StringContent Json<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        Json(JsonSerializer.Serialize(value, typeInfo));

    /// <summary>A write request with the antiforgery token and the owner header, both optional.</summary>
    public static HttpRequestMessage Write(HttpMethod method, string path, HttpContent content, string? token, long? owner, string? offset = null)
    {
        HttpRequestMessage request = new(method, path) { Content = content };
        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation(TokenHeader, token);
        }

        if (owner is { } id)
        {
            request.Headers.TryAddWithoutValidation(OwnerHeader, id.ToString(CultureInfo.InvariantCulture));
        }

        if (offset is not null)
        {
            request.Headers.TryAddWithoutValidation(OffsetHeader, offset);
        }

        return request;
    }

    public static AnswerDto Answer(string questionId, string grade = "knew", DateTimeOffset? answeredAt = null, string? clientAnswerId = null) =>
        new(clientAnswerId ?? NewUlid(), questionId, grade, false, "practice", null, answeredAt ?? DateTimeOffset.UtcNow.AddMinutes(-1));

    public static StringContent Batch(params AnswerDto[] answers) =>
        Json(new AnswerBatch(AnswerBatch.CurrentVersion, answers), TrainerJsonContext.Default.AnswerBatch);

    /// <summary>Posts a batch with a fresh token and the right owner.</summary>
    public static async Task<HttpResponseMessage> PostBatchAsync(HttpClient client, long owner, CancellationToken ct, params AnswerDto[] answers)
    {
        string token = await GetTokenAsync(client, ct);
        using HttpRequestMessage request = Write(HttpMethod.Post, "/api/answers/batch", Batch(answers), token, owner);
        return await client.SendAsync(request, ct);
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        string body = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize(body, typeInfo) ?? throw new InvalidOperationException("The body was JSON null.");
    }

    /// <summary>Asserts a JSON <see cref="ApiErrorDto"/> body with the status and code, never an HTML page.</summary>
    public static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code, CancellationToken ct)
    {
        string body = await response.Content.ReadAsStringAsync(ct);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("<html", body, StringComparison.OrdinalIgnoreCase);
        ApiErrorDto error = JsonSerializer.Deserialize(body, TrainerJsonContext.Default.ApiErrorDto)
            ?? throw new InvalidOperationException("The error body was JSON null.");
        Assert.Equal(code, error.Code);
    }

    public static PracticeSessionRequest Practice(
        string status = "all", int count = 5, IReadOnlyList<string>? topics = null, string culture = "en") =>
        new(culture, "dotnet", [], topics ?? [], [], status, count, null, []);

    public static async Task<UserAccess> AccessOfAsync(TrainerWebFactory factory, long telegramId, CancellationToken ct)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IUserAccountService>().GetAccessAsync(telegramId, ct)
            ?? throw new InvalidOperationException("The user does not exist.");
    }

    public static async Task<int> CountAsync(TrainerWebFactory factory, FormattableString sql, CancellationToken ct)
    {
        await using TrainerDbContext db = PostgresFixture.CreateContext(factory.ConnectionString);
        return await db.Database.SqlQuery<int>(sql).SingleAsync(ct);
    }

    /// <summary>
    /// A <c>Cookie</c> header with a ticket the app itself protects, checked "just now", so no database check runs. For hosts
    /// that cannot sign in through the fake provider (any environment but Testing).
    /// </summary>
    public static string ForgeSessionCookie(TrainerWebFactory factory, long telegramId)
    {
        ClaimsIdentity identity = new(
            [
                new Claim(TrainerClaimTypes.UserId, NewUlid()),
                new Claim(TrainerClaimTypes.TelegramUserId, telegramId.ToString(CultureInfo.InvariantCulture)),
                new Claim(TrainerClaimTypes.Subject, "forged-" + telegramId.ToString(CultureInfo.InvariantCulture)),
            ],
            TelegramAuth.CookieScheme);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        AuthenticationProperties properties = new() { IsPersistent = true, IssuedUtc = now, ExpiresUtc = now.AddDays(30) };
        properties.Items[TrainerCookieEvents.LastCheckedKey] = now.ToString("O", CultureInfo.InvariantCulture);
        CookieAuthenticationOptions options = factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(TelegramAuth.CookieScheme);
        string ticket = options.TicketDataFormat.Protect(new AuthenticationTicket(new ClaimsPrincipal(identity), properties, TelegramAuth.CookieScheme));
        return $"{AuthCookieNames.Session}={ticket}";
    }

    public static void AcceptGzip(HttpRequestMessage request) =>
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
}
