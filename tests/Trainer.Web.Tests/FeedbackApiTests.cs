using System.Net;
using Trainer.Contracts;
using Trainer.TestSupport;
using static Trainer.Web.Tests.ApiTestHelpers;

namespace Trainer.Web.Tests;

/// <summary>One site that allows a single new report per day, so the limit is reachable in a test.</summary>
public sealed class FeedbackSiteFixture(PostgresFixture postgres) : IAsyncLifetime
{
    public FakeTelegramOidcServer Telegram { get; private set; } = null!;

    public TrainerWebFactory Factory { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Telegram = await FakeTelegramOidcServer.StartAsync(TrainerWebFactoryOptions.TelegramClientId);
        Factory = await TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions
        {
            Telegram = Telegram,
            Settings = new Dictionary<string, string?> { ["App:Feedback:ReportsPerDay"] = "1" },
        });
        await Factory.WaitUntilReadyAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Telegram.DisposeAsync();
    }
}

/// <summary>Rating and report endpoints (ADR 0016): the write conventions of ADR 0008, validation and the report limit.</summary>
public sealed class FeedbackApiTests(FeedbackSiteFixture site) : IClassFixture<FeedbackSiteFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string RatingPath(string id = ClosureQuestionId) => $"/api/questions/{id}/rating";

    private static string ReportPath(string id = ClosureQuestionId) => $"/api/questions/{id}/reports";

    private static StringContent RatingBody(string value) => Json(new RatingRequest(value), TrainerJsonContext.Default.RatingRequest);

    private static StringContent ReportBody(string culture, string reason, string? comment = null) =>
        Json(new ReportRequest(culture, reason, comment), TrainerJsonContext.Default.ReportRequest);

    // The first question opens the session whatever the shuffle says, so the rated one is always in it.
    private static StringContent PracticeBody() => Json(
        new PracticeSessionRequest("en", "dotnet", [], [], [], "all", 5, ClosureQuestionId, []), TrainerJsonContext.Default.PracticeSessionRequest);

    private Task<int> ReportsOfAsync(long telegramId) => CountAsync(
        site.Factory,
        $"""SELECT count(*)::int AS "Value" FROM question_reports r JOIN users u ON u.id = r.user_id WHERE u.telegram_user_id = {telegramId}""",
        Ct);

    private Task<int> RatingsOfAsync(long telegramId) => CountAsync(
        site.Factory,
        $"""SELECT count(*)::int AS "Value" FROM question_ratings r JOIN users u ON u.id = r.user_id WHERE u.telegram_user_id = {telegramId}""",
        Ct);

    public static TheoryData<string> Routes() => new() { "put-rating", "delete-rating", "post-report" };

    private static HttpRequestMessage ForRoute(string route, string? token, long? owner) => route switch
    {
        "put-rating" => Write(HttpMethod.Put, RatingPath(), RatingBody("like"), token, owner),
        "delete-rating" => Write(HttpMethod.Delete, RatingPath(), content: null!, token, owner),
        _ => Write(HttpMethod.Post, ReportPath(), ReportBody("en", "other", "x"), token, owner),
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryRoute_Anonymous_Returns401(string route)
    {
        using HttpClient client = SignInTestHelpers.Browser(site.Factory);
        string token = await GetTokenAsync(client, Ct);
        using HttpRequestMessage request = ForRoute(route, token, owner: 1);

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, ApiErrorCodes.SignInRequired, Ct);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryRoute_WithoutAntiforgeryHeader_Returns400(string route)
    {
        const long Owner = 700_021L;
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, Owner, Ct);
        using HttpRequestMessage request = ForRoute(route, token: null, Owner);

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, ApiErrorCodes.Antiforgery, Ct);
        Assert.Equal(0, await RatingsOfAsync(Owner));
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryRoute_OwnerHeaderOfOtherUser_Returns409AndStoresNothing(string route)
    {
        const long Owner = 700_022L;
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, Owner, Ct);
        string token = await GetTokenAsync(client, Ct);
        using HttpRequestMessage request = ForRoute(route, token, owner: 700_999L);

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        await AssertErrorAsync(response, HttpStatusCode.Conflict, ApiErrorCodes.OwnerMismatch, Ct);
        Assert.Equal(0, await RatingsOfAsync(Owner));
        Assert.Equal(0, await ReportsOfAsync(Owner));
    }

    [Theory]
    [InlineData("put-rating")]
    [InlineData("delete-rating")]
    [InlineData("post-report")]
    public async Task EveryRoute_BadId_Returns400(string route)
    {
        const long Owner = 700_023L;
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, Owner, Ct);
        string token = await GetTokenAsync(client, Ct);
        using HttpRequestMessage request = route switch
        {
            "put-rating" => Write(HttpMethod.Put, RatingPath("nope"), RatingBody("like"), token, Owner),
            "delete-rating" => Write(HttpMethod.Delete, RatingPath("nope"), content: null!, token, Owner),
            _ => Write(HttpMethod.Post, ReportPath("nope"), ReportBody("en", "other"), token, Owner),
        };

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, ApiErrorCodes.Validation, Ct);
    }

    [Fact]
    public async Task SignedInSession_WithMyRatings_IsNoStoreAndSetsNoCookie()
    {
        const long Owner = 700_024L;
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, Owner, Ct);

        using HttpResponseMessage response = await client.PostAsync("/api/sessions/practice", PracticeBody(), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore, "A response carrying the user's ratings must not be stored.");
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Rating_Anonymous_Returns401()
    {
        using HttpClient client = SignInTestHelpers.Browser(site.Factory);
        string token = await GetTokenAsync(client, Ct);
        using HttpRequestMessage request = Write(HttpMethod.Put, RatingPath(), RatingBody("like"), token, owner: 1);

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, ApiErrorCodes.SignInRequired, Ct);
    }

    [Fact]
    public async Task Rating_WithoutAntiforgeryHeader_Returns400()
    {
        const long Owner = 700_001L;
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, Owner, Ct);
        using HttpRequestMessage request = Write(HttpMethod.Put, RatingPath(), RatingBody("like"), token: null, Owner);

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, ApiErrorCodes.Antiforgery, Ct);
        Assert.Equal(0, await RatingsOfAsync(Owner));
    }

    [Fact]
    public async Task Rating_OwnerHeaderOfOtherUser_Returns409AndStoresNothing()
    {
        const long Owner = 700_002L;
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, Owner, Ct);
        string token = await GetTokenAsync(client, Ct);
        using HttpRequestMessage request = Write(HttpMethod.Put, RatingPath(), RatingBody("like"), token, owner: 700_999L);

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        await AssertErrorAsync(response, HttpStatusCode.Conflict, ApiErrorCodes.OwnerMismatch, Ct);
        Assert.Equal(0, await RatingsOfAsync(Owner));
    }

    [Fact]
    public async Task Rating_PutThenDelete_RoundTrips()
    {
        const long Owner = 700_003L;
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, Owner, Ct);
        string token = await GetTokenAsync(client, Ct);

        using (HttpRequestMessage put = Write(HttpMethod.Put, RatingPath(), RatingBody("dislike"), token, Owner))
        using (HttpResponseMessage response = await client.SendAsync(put, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("dislike", (await ReadAsync(response, TrainerJsonContext.Default.RatingDto, Ct)).Value);
        }

        Assert.Equal(1, await RatingsOfAsync(Owner));

        using (HttpRequestMessage delete = Write(HttpMethod.Delete, RatingPath(), content: null!, token, Owner))
        using (HttpResponseMessage response = await client.SendAsync(delete, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Null((await ReadAsync(response, TrainerJsonContext.Default.RatingDto, Ct)).Value);
        }

        Assert.Equal(0, await RatingsOfAsync(Owner));
    }

    [Theory]
    [InlineData("LIKE")]
    [InlineData("love")]
    [InlineData("")]
    public async Task Rating_UnknownValue_Returns400(string value)
    {
        const long Owner = 700_004L;
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, Owner, Ct);
        string token = await GetTokenAsync(client, Ct);
        using HttpRequestMessage request = Write(HttpMethod.Put, RatingPath(), RatingBody(value), token, Owner);

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, ApiErrorCodes.Validation, Ct);
    }

    [Fact]
    public async Task Rating_BadIdOrUnknownQuestion_Returns400Or404()
    {
        const long Owner = 700_005L;
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, Owner, Ct);
        string token = await GetTokenAsync(client, Ct);

        using (HttpRequestMessage bad = Write(HttpMethod.Put, RatingPath("not-a-ulid"), RatingBody("like"), token, Owner))
        using (HttpResponseMessage response = await client.SendAsync(bad, Ct))
        {
            await AssertErrorAsync(response, HttpStatusCode.BadRequest, ApiErrorCodes.Validation, Ct);
        }

        using (HttpRequestMessage unknown = Write(HttpMethod.Put, RatingPath(NewUlid()), RatingBody("like"), token, Owner))
        using (HttpResponseMessage response = await client.SendAsync(unknown, Ct))
        {
            await AssertErrorAsync(response, HttpStatusCode.NotFound, ApiErrorCodes.QuestionNotFound, Ct);
        }

        Assert.Equal(0, await RatingsOfAsync(Owner));
    }

    [Fact]
    public async Task Report_NewThenRepeat_Returns201ThenUpdates()
    {
        const long Owner = 700_006L;
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, Owner, Ct);
        string token = await GetTokenAsync(client, Ct);

        using (HttpRequestMessage first = Write(HttpMethod.Post, ReportPath(), ReportBody("en", "wrong-answer", "first"), token, Owner))
        using (HttpResponseMessage response = await client.SendAsync(first, Ct))
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal(ReportResponse.Created, (await ReadAsync(response, TrainerJsonContext.Default.ReportResponse, Ct)).Outcome);
        }

        // Editing the open report does not count against the limit of one per day.
        using (HttpRequestMessage again = Write(HttpMethod.Post, ReportPath(), ReportBody("ru", "wrong-answer", "second"), token, Owner))
        using (HttpResponseMessage response = await client.SendAsync(again, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(ReportResponse.Updated, (await ReadAsync(response, TrainerJsonContext.Default.ReportResponse, Ct)).Outcome);
        }
    }

    [Fact]
    public async Task Report_OverLimit_Returns429WithRetryAfter()
    {
        const long Owner = 700_007L;
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, Owner, Ct);
        string token = await GetTokenAsync(client, Ct);

        using (HttpRequestMessage first = Write(HttpMethod.Post, ReportPath(), ReportBody("en", "outdated"), token, Owner))
        using (HttpResponseMessage response = await client.SendAsync(first, Ct))
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        using HttpRequestMessage second = Write(HttpMethod.Post, ReportPath(), ReportBody("en", "broken-link"), token, Owner);
        using HttpResponseMessage limited = await client.SendAsync(second, Ct);

        await AssertErrorAsync(limited, HttpStatusCode.TooManyRequests, ApiErrorCodes.ReportLimit, Ct);
        TimeSpan retryAfter = limited.Headers.RetryAfter?.Delta ?? throw new InvalidOperationException("No Retry-After header.");
        Assert.InRange(retryAfter, TimeSpan.FromSeconds(1), TimeSpan.FromHours(24));
    }

    [Theory]
    [InlineData("like", "spam")]
    [InlineData("xx", "other")]
    [InlineData("en", "Other")]
    public async Task Report_UnknownReasonOrCulture_Returns400(string culture, string reason)
    {
        const long Owner = 700_008L;
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, Owner, Ct);
        string token = await GetTokenAsync(client, Ct);
        using HttpRequestMessage request = Write(HttpMethod.Post, ReportPath(), ReportBody(culture, reason), token, Owner);

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, ApiErrorCodes.Validation, Ct);
    }

    [Fact]
    public async Task Report_CommentOver1000_Returns400_AndUnknownQuestionReturns404()
    {
        const long Owner = 700_009L;
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, Owner, Ct);
        string token = await GetTokenAsync(client, Ct);

        using (HttpRequestMessage tooLong = Write(HttpMethod.Post, ReportPath(), ReportBody("en", "other", new string('x', 1001)), token, Owner))
        using (HttpResponseMessage response = await client.SendAsync(tooLong, Ct))
        {
            await AssertErrorAsync(response, HttpStatusCode.BadRequest, ApiErrorCodes.Validation, Ct);
        }

        using (HttpRequestMessage unknown = Write(HttpMethod.Post, ReportPath(NewUlid()), ReportBody("en", "other", "x"), token, Owner))
        using (HttpResponseMessage response = await client.SendAsync(unknown, Ct))
        {
            await AssertErrorAsync(response, HttpStatusCode.NotFound, ApiErrorCodes.QuestionNotFound, Ct);
        }
    }

    [Fact]
    public async Task PracticeSession_SignedIn_CarriesMyRatings_AnonymousCarriesNull()
    {
        const long Owner = 700_010L;
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, Owner, Ct);
        string token = await GetTokenAsync(client, Ct);
        using (HttpRequestMessage put = Write(HttpMethod.Put, RatingPath(), RatingBody("like"), token, Owner))
        using (HttpResponseMessage response = await client.SendAsync(put, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using HttpResponseMessage signedIn = await client.PostAsync(
            "/api/sessions/practice", PracticeBody(), Ct);
        SessionDto session = await ReadAsync(signedIn, TrainerJsonContext.Default.SessionDto, Ct);
        Assert.Contains(session.Questions, q => q.Id == ClosureQuestionId);
        Assert.Equal(new Dictionary<string, string> { [ClosureQuestionId] = "like" }, session.MyRatings);

        using HttpClient anonymous = SignInTestHelpers.Browser(site.Factory);
        using HttpResponseMessage anonymousResponse = await anonymous.PostAsync(
            "/api/sessions/practice", PracticeBody(), Ct);
        Assert.Null((await ReadAsync(anonymousResponse, TrainerJsonContext.Default.SessionDto, Ct)).MyRatings);
    }
}
