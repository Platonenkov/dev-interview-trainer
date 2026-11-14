using System.Net;
using Trainer.Contracts;
using Trainer.TestSupport;
using static Trainer.Web.Tests.ApiTestHelpers;

namespace Trainer.Web.Tests;

/// <summary>Spec 8.8: every write validates the token from <c>GET /api/antiforgery</c>, sent in <c>X-CSRF-TOKEN</c>.</summary>
public sealed class ApiAntiforgeryTests(SignInSiteFixture site) : IClassFixture<SignInSiteFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Antiforgery_ReturnsTokenNoStoreAndSetsCookie()
    {
        using HttpClient client = SignInTestHelpers.Browser(site.Factory);

        using HttpResponseMessage response = await client.GetAsync("/api/antiforgery", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AntiforgeryTokenDto token = await ReadAsync(response, TrainerJsonContext.Default.AntiforgeryTokenDto, Ct);
        Assert.False(string.IsNullOrEmpty(token.RequestToken));
        Assert.Equal(TokenHeader, token.HeaderName);
        Assert.True(response.Headers.CacheControl?.NoStore, "The token response must be no-store.");
        Assert.NotNull(SignInTestHelpers.SetCookie(response, ".dit.af"));
    }

    [Fact]
    public async Task Antiforgery_IsNotCompressed()
    {
        using HttpClient client = SignInTestHelpers.Browser(site.Factory);
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/antiforgery");
        AcceptGzip(request);

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Content.Headers.ContentEncoding);
    }

    [Fact]
    public async Task BatchWithoutToken_Returns400Antiforgery()
    {
        const long Owner = 610_001L;
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, Owner, Ct);
        using HttpRequestMessage request = Write(HttpMethod.Post, "/api/answers/batch", Batch(Answer(ClosureQuestionId)), token: null, Owner);

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, ApiErrorCodes.Antiforgery, Ct);
        Assert.Equal(0, await CountAsync(site.Factory, $"""SELECT count(*)::int AS "Value" FROM answers a JOIN users u ON u.id = a.user_id WHERE u.telegram_user_id = {Owner}""", Ct));
    }

    [Fact]
    public async Task BatchWithAnonymousToken_AfterSignIn_Returns400Antiforgery()
    {
        const long Owner = 610_002L;
        using HttpClient client = SignInTestHelpers.Browser(site.Factory);
        string anonymousToken = await GetTokenAsync(client, Ct);
        using HttpResponseMessage callback = await TelegramSignInDriver.SignInAsync(
            client, site.Telegram, new FakeTelegramIdentity(Owner, "Token Reuser", null), "/en", Ct);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        using HttpRequestMessage request = Write(HttpMethod.Post, "/api/answers/batch", Batch(Answer(ClosureQuestionId)), anonymousToken, Owner);

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, ApiErrorCodes.Antiforgery, Ct);
    }

    [Fact]
    public async Task BatchWithFreshToken_Accepts()
    {
        const long Owner = 610_003L;
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, Owner, Ct);
        AnswerDto answer = Answer(ClosureQuestionId);

        using HttpResponseMessage response = await PostBatchAsync(client, Owner, Ct, answer);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AnswerBatchResponse body = await ReadAsync(response, TrainerJsonContext.Default.AnswerBatchResponse, Ct);
        Assert.Equal([answer.ClientAnswerId], body.Accepted);
        Assert.Empty(body.Rejected);
    }
}
