using System.Net;
using System.Text;
using Trainer.Contracts;
using Trainer.TestSupport;
using static Trainer.Web.Tests.ApiTestHelpers;

namespace Trainer.Web.Tests;

/// <summary>
/// Session reads are <c>POST</c> JSON without antiforgery (ADR 0008): only <c>application/json</c> binds and no CORS
/// policy exists, so a cross-site page can neither send them without a preflight nor read the answer.
/// </summary>
public sealed class SessionApiTests(SignInSiteFixture site) : IClassFixture<SignInSiteFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Anonymous() => SignInTestHelpers.Browser(site.Factory);

    private static StringContent PracticeBody(PracticeSessionRequest request) => Json(request, TrainerJsonContext.Default.PracticeSessionRequest);

    [Fact]
    public async Task Practice_Anonymous_ReturnsSession()
    {
        using HttpClient client = Anonymous();

        using HttpResponseMessage response = await client.PostAsync("/api/sessions/practice", PracticeBody(Practice(count: 3)), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore, "Every /api response is no-store.");
        SessionDto session = await ReadAsync(response, TrainerJsonContext.Default.SessionDto, Ct);
        Assert.Equal(3, session.Questions.Count);
        Assert.True(session.PoolSize >= 3);
    }

    [Fact]
    public async Task Practice_AnonymousDue_Returns401()
    {
        using HttpClient client = Anonymous();

        using HttpResponseMessage response = await client.PostAsync("/api/sessions/practice", PracticeBody(Practice(status: "due")), Ct);

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, ApiErrorCodes.SignInRequired, Ct);
    }

    public static TheoryData<string, string?> NonJson() => new()
    {
        { "/api/sessions/practice", "text/plain" },
        { "/api/sessions/practice", "multipart/form-data; boundary=x" },
        { "/api/sessions/practice", "application/x-www-form-urlencoded" },
        { "/api/sessions/practice", null },
        { "/api/sessions/mock", "text/plain" },
        { "/api/sessions/mock", "multipart/form-data; boundary=x" },
        { "/api/sessions/mock", "application/x-www-form-urlencoded" },
        { "/api/sessions/mock", null },
        { "/api/sessions/review", "text/plain" },
        { "/api/sessions/review", "multipart/form-data; boundary=x" },
        { "/api/sessions/review", "application/x-www-form-urlencoded" },
        { "/api/sessions/review", null },
    };

    [Theory]
    [MemberData(nameof(NonJson))]
    public async Task Sessions_RejectNonJson(string path, string? contentType)
    {
        // Signed in, so review reaches binding instead of stopping at 401.
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, 630_001L, Ct);
        ByteArrayContent content = new(Encoding.UTF8.GetBytes("""{"culture":"en","stack":"dotnet","level":"middle","topics":[],"count":5}"""));
        if (contentType is not null)
        {
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        using HttpResponseMessage response = await client.PostAsync(path, content, Ct);

        await AssertErrorAsync(response, HttpStatusCode.UnsupportedMediaType, ApiErrorCodes.UnsupportedMediaType, Ct);
    }

    [Fact]
    public async Task Sessions_CrossOriginPreflight_IsNotGranted()
    {
        using HttpClient client = Anonymous();
        using HttpRequestMessage request = new(HttpMethod.Options, "/api/sessions/practice");
        request.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "content-type");

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        Assert.False(response.IsSuccessStatusCode, $"The preflight must not succeed, got {(int)response.StatusCode}.");
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Methods"));
    }

    [Fact]
    public async Task Practice_UnknownTopicDropped_CountClamped()
    {
        using HttpClient client = Anonymous();

        using HttpResponseMessage all = await client.PostAsync(
            "/api/sessions/practice", PracticeBody(Practice(count: 1000, topics: ["async", "no-such-topic"])), Ct);
        using HttpResponseMessage one = await client.PostAsync(
            "/api/sessions/practice", PracticeBody(Practice(count: 0, topics: ["async"])), Ct);

        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        SessionDto session = await ReadAsync(all, TrainerJsonContext.Default.SessionDto, Ct);
        Assert.NotEmpty(session.Questions);
        Assert.All(session.Questions, q => Assert.Equal("async", q.Topic));
        Assert.Equal(session.PoolSize, session.Questions.Count);
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.Single((await ReadAsync(one, TrainerJsonContext.Default.SessionDto, Ct)).Questions);
    }

    [Fact]
    public async Task Review_Anonymous_Returns401Json()
    {
        using HttpClient client = Anonymous();

        using HttpResponseMessage response = await client.PostAsync(
            "/api/sessions/review", Json(new ReviewSessionRequest("en", "dotnet", 10), TrainerJsonContext.Default.ReviewSessionRequest), Ct);

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, ApiErrorCodes.SignInRequired, Ct);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Review_SignedIn_ReturnsDue()
    {
        const long Owner = 630_002L;
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, Owner, Ct);
        // "didnt-know" schedules the next review one day after the answer, so an answer from two days ago is due now.
        using (HttpResponseMessage batch = await PostBatchAsync(
            client, Owner, Ct, Answer(ClosureQuestionId, grade: "didnt-know", answeredAt: DateTimeOffset.UtcNow.AddDays(-2))))
        {
            Assert.Equal(HttpStatusCode.OK, batch.StatusCode);
        }

        using HttpResponseMessage response = await client.PostAsync(
            "/api/sessions/review", Json(new ReviewSessionRequest("en", "dotnet", 10), TrainerJsonContext.Default.ReviewSessionRequest), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        SessionDto session = await ReadAsync(response, TrainerJsonContext.Default.SessionDto, Ct);
        Assert.Equal([ClosureQuestionId], session.Questions.Select(q => q.Id));
    }

    [Fact]
    public async Task Mock_Anonymous_ReturnsSession()
    {
        using HttpClient client = Anonymous();

        using HttpResponseMessage response = await client.PostAsync(
            "/api/sessions/mock", Json(new MockSessionRequest("en", "dotnet", "middle", [], 5), TrainerJsonContext.Default.MockSessionRequest), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        MockSessionDto session = await ReadAsync(response, TrainerJsonContext.Default.MockSessionDto, Ct);
        Assert.NotEmpty(session.Questions);
    }

    [Fact]
    public async Task SignedInSessionJson_IsCompressed()
    {
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, 630_003L, Ct);
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/sessions/practice") { Content = PracticeBody(Practice(count: 5)) };
        AcceptGzip(request);

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("gzip", response.Content.Headers.ContentEncoding);
    }

    private static HttpRequestMessage PracticeFor(string? owner)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/api/sessions/practice") { Content = PracticeBody(Practice(count: 2)) };
        if (owner is not null)
        {
            request.Headers.TryAddWithoutValidation(OwnerHeader, owner);
        }

        return request;
    }

    [Theory]
    [InlineData(null, HttpStatusCode.OK)]
    [InlineData("anon", HttpStatusCode.OK)]
    // A page bound to a user whose session is gone (expired, or ended in another tab) is told the session ended.
    [InlineData("680001", HttpStatusCode.Unauthorized)]
    [InlineData("junk", HttpStatusCode.Conflict)]
    public async Task Practice_Anonymous_ChecksTheTabsOwner(string? owner, HttpStatusCode expected)
    {
        using HttpClient client = Anonymous();
        using HttpRequestMessage request = PracticeFor(owner);

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        if (expected == HttpStatusCode.Conflict)
        {
            await AssertErrorAsync(response, HttpStatusCode.Conflict, ApiErrorCodes.OwnerMismatch, Ct);
        }
        else if (expected == HttpStatusCode.Unauthorized)
        {
            await AssertErrorAsync(response, HttpStatusCode.Unauthorized, ApiErrorCodes.SignInRequired, Ct);
        }
        else
        {
            Assert.Equal(expected, response.StatusCode);
        }
    }

    [Theory]
    [InlineData(null, HttpStatusCode.OK)]
    [InlineData("680002", HttpStatusCode.OK)]
    [InlineData("anon", HttpStatusCode.Conflict)]
    [InlineData("680003", HttpStatusCode.Conflict)]
    public async Task Practice_SignedIn_ChecksTheTabsOwner(string? owner, HttpStatusCode expected)
    {
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, 680_002L, Ct);
        using HttpRequestMessage request = PracticeFor(owner);

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        if (expected == HttpStatusCode.Conflict)
        {
            await AssertErrorAsync(response, HttpStatusCode.Conflict, ApiErrorCodes.OwnerMismatch, Ct);
        }
        else
        {
            Assert.Equal(expected, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("/api/sessions/mock")]
    [InlineData("/api/sessions/review")]
    public async Task OtherSessionReads_SignedIn_RefuseAnotherOwner(string path)
    {
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, 680_004L, Ct);
        string body = path.EndsWith("mock", StringComparison.Ordinal)
            ? "{\"culture\":\"en\",\"stack\":\"dotnet\",\"level\":\"middle\",\"topics\":[],\"count\":5}"
            : "{\"culture\":\"en\",\"stack\":\"dotnet\",\"count\":5}";
        using HttpRequestMessage request = new(HttpMethod.Post, path) { Content = Json(body) };
        request.Headers.TryAddWithoutValidation(OwnerHeader, "680005");

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        await AssertErrorAsync(response, HttpStatusCode.Conflict, ApiErrorCodes.OwnerMismatch, Ct);
    }
}
