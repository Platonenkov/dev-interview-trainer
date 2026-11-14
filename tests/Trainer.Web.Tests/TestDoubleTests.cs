using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Trainer.TestSupport;

namespace Trainer.Web.Tests;

public sealed class TestDoubleTests
{
    private const string ClientId = "1000";
    private const string RedirectUri = "https://localhost/signin-telegram";
    private const string DefaultVerifier = "default-verifier-default-verifier-default-verifier-0000";

    [Fact]
    public async Task FakeServer_Discovery_ListsEndpointsAndIssuer()
    {
        await using FakeTelegramOidcServer server = await FakeTelegramOidcServer.StartAsync(ClientId);
        using HttpClient http = CreateHttpClient();

        using JsonDocument document = JsonDocument.Parse(await http.GetStringAsync(server.MetadataAddress, TestContext.Current.CancellationToken));
        JsonElement root = document.RootElement;

        string issuer = root.GetProperty("issuer").GetString()!;
        Assert.Equal(server.MetadataAddress.GetLeftPart(UriPartial.Authority), issuer);
        Assert.StartsWith(issuer + "/", root.GetProperty("authorization_endpoint").GetString(), StringComparison.Ordinal);
        Assert.StartsWith(issuer + "/", root.GetProperty("token_endpoint").GetString(), StringComparison.Ordinal);
        Assert.StartsWith(issuer + "/", root.GetProperty("jwks_uri").GetString(), StringComparison.Ordinal);
        Assert.Equal(
            ["RS256"],
            root.GetProperty("id_token_signing_alg_values_supported").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public async Task FakeServer_IdToken_ValidatesAgainstJwksAndCarriesNonce()
    {
        await using FakeTelegramOidcServer server = await FakeTelegramOidcServer.StartAsync(ClientId);
        server.NextIdentity = new FakeTelegramIdentity(424242, "Ada Lovelace", "ada");
        using HttpClient http = CreateHttpClient();

        Uri location = await AuthorizeAsync(http, server, "nonce-1", "state-1");
        Dictionary<string, StringValues> callback = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("state-1", callback["state"].ToString());

        using HttpResponseMessage tokenResponse = await ExchangeAsync(http, server, callback["code"].ToString());
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        using JsonDocument tokens = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        string idToken = tokens.RootElement.GetProperty("id_token").GetString()!;

        JsonWebKeySet keys = new(await http.GetStringAsync(new Uri(server.MetadataAddress, "/jwks"), TestContext.Current.CancellationToken));
        TokenValidationResult result = await new JsonWebTokenHandler().ValidateTokenAsync(
            idToken,
            new TokenValidationParameters
            {
                ValidIssuer = server.MetadataAddress.GetLeftPart(UriPartial.Authority),
                ValidAudience = ClientId,
                IssuerSigningKeys = keys.GetSigningKeys(),
                ValidAlgorithms = ["RS256"],
            });

        Assert.True(result.IsValid, result.Exception?.Message);
        JsonWebToken token = (JsonWebToken)result.SecurityToken;
        Assert.Equal("nonce-1", token.GetClaim("nonce").Value);
        Assert.Equal(424242L, token.GetPayloadValue<long>("id"));
        Assert.Equal("Ada Lovelace", token.GetClaim("name").Value);
        Assert.Equal("ada", token.GetClaim("preferred_username").Value);
        Assert.NotEqual("424242", token.Subject);
    }

    [Fact]
    public async Task FakeServer_IdToken_OmitsNameClaimsThatAreNull()
    {
        await using FakeTelegramOidcServer server = await FakeTelegramOidcServer.StartAsync(ClientId);
        server.NextIdentity = new FakeTelegramIdentity(7, null, null);
        using HttpClient http = CreateHttpClient();

        Uri location = await AuthorizeAsync(http, server, "n", "s");
        using HttpResponseMessage tokenResponse = await ExchangeAsync(http, server, QueryHelpers.ParseQuery(location.Query)["code"].ToString());
        using JsonDocument tokens = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        JsonWebToken token = new(tokens.RootElement.GetProperty("id_token").GetString()!);

        Assert.False(token.TryGetPayloadValue<string>("name", out _));
        Assert.False(token.TryGetPayloadValue<string>("preferred_username", out _));
    }

    [Fact]
    public async Task FakeServer_CodeIsSingleUse()
    {
        await using FakeTelegramOidcServer server = await FakeTelegramOidcServer.StartAsync(ClientId);
        using HttpClient http = CreateHttpClient();
        Uri location = await AuthorizeAsync(http, server, "n", "s");
        string code = QueryHelpers.ParseQuery(location.Query)["code"].ToString();

        using HttpResponseMessage first = await ExchangeAsync(http, server, code);
        using HttpResponseMessage second = await ExchangeAsync(http, server, code);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Contains("invalid_grant", await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FakeServer_FailNext_RedirectsWithAccessDenied()
    {
        await using FakeTelegramOidcServer server = await FakeTelegramOidcServer.StartAsync(ClientId);
        using HttpClient http = CreateHttpClient();

        server.FailNext = true;
        Uri denied = await AuthorizeAsync(http, server, "n", "state-9");
        Uri recovered = await AuthorizeAsync(http, server, "n", "state-10");

        Dictionary<string, StringValues> query = QueryHelpers.ParseQuery(denied.Query);
        Assert.Equal("access_denied", query["error"].ToString());
        Assert.Equal("state-9", query["state"].ToString());
        Assert.False(query.ContainsKey("code"));
        Assert.False(server.FailNext);
        Assert.True(QueryHelpers.ParseQuery(recovered.Query).ContainsKey("code"));
    }

    [Fact]
    public async Task FakeServer_FailNextWith_RedirectsWithServerErrorAndDescription()
    {
        await using FakeTelegramOidcServer server = await FakeTelegramOidcServer.StartAsync(ClientId);
        using HttpClient http = CreateHttpClient();

        server.FailNextWith("boom & more");
        Uri location = await AuthorizeAsync(http, server, "n", "state-3");

        Dictionary<string, StringValues> query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("server_error", query["error"].ToString());
        Assert.Equal("boom & more", query["error_description"].ToString());
        Assert.Equal("state-3", query["state"].ToString());
    }

    [Fact]
    public async Task FakeServer_Token_RejectsWrongPkceVerifier()
    {
        await using FakeTelegramOidcServer server = await FakeTelegramOidcServer.StartAsync(ClientId);
        using HttpClient http = CreateHttpClient();
        const string verifier = "correct-verifier-correct-verifier-correct-verifier-1";
        Uri location = await AuthorizeAsync(http, server, "n", "s", PkceChallenge(verifier));
        string code = QueryHelpers.ParseQuery(location.Query)["code"].ToString();

        using HttpResponseMessage wrong = await ExchangeAsync(http, server, code, "wrong-verifier");

        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
    }

    [Fact]
    public async Task FakeServer_Authorize_RequiresAPkceChallenge()
    {
        await using FakeTelegramOidcServer server = await FakeTelegramOidcServer.StartAsync(ClientId);
        using HttpClient http = CreateHttpClient();
        Uri authorize = new(QueryHelpers.AddQueryString(new Uri(server.MetadataAddress, "/auth").ToString(), new Dictionary<string, string?>
        {
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["response_type"] = "code",
            ["nonce"] = "n",
            ["state"] = "s",
        }));

        using HttpResponseMessage response = await http.GetAsync(authorize, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("wrong-secret", "authorization_code")]
    [InlineData(null, "client_credentials")]
    public async Task FakeServer_Token_RejectsWrongSecretOrGrantType(string? secret, string grantType)
    {
        await using FakeTelegramOidcServer server = await FakeTelegramOidcServer.StartAsync(ClientId);
        using HttpClient http = CreateHttpClient();
        Uri location = await AuthorizeAsync(http, server, "n", "s");
        string code = QueryHelpers.ParseQuery(location.Query)["code"].ToString();

        using HttpResponseMessage response = await ExchangeAsync(http, server, code, secret: secret, grantType: grantType);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FakeServer_Token_AcceptsBasicClientAuthentication()
    {
        await using FakeTelegramOidcServer server = await FakeTelegramOidcServer.StartAsync(ClientId);
        using HttpClient http = CreateHttpClient();
        Uri location = await AuthorizeAsync(http, server, "n", "s");
        using FormUrlEncodedContent content = new(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = QueryHelpers.ParseQuery(location.Query)["code"].ToString(),
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = DefaultVerifier,
        });
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(server.MetadataAddress, "/token")) { Content = content };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(ClientId + ":" + server.ClientSecret)));

        using HttpResponseMessage response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void SessionCookieReader_ReassemblesChunkedTicket_AndRejectsMissingChunks()
    {
        ServiceCollection services = new();
        services.AddDataProtection();
        services.AddAuthentication("TelegramCookie").AddCookie("TelegramCookie", options => options.Cookie.Name = ".dit.session");
        using ServiceProvider provider = services.BuildServiceProvider();
        CookieAuthenticationOptions options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<CookieAuthenticationOptions>>().Get("TelegramCookie");
        AuthenticationTicket ticket = new(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("telegram_user_id", "99")], "TelegramCookie")), "TelegramCookie");
        string protectedTicket = options.TicketDataFormat.Protect(ticket);
        int half = protectedTicket.Length / 2;

        using HttpResponseMessage chunked = new();
        chunked.Headers.Add("Set-Cookie", ".dit.session=chunks-2; path=/");
        chunked.Headers.Add("Set-Cookie", $".dit.sessionC1={protectedTicket[..half]}; path=/");
        chunked.Headers.Add("Set-Cookie", $".dit.sessionC2={protectedTicket[half..]}; path=/");
        using HttpResponseMessage missing = new();
        missing.Headers.Add("Set-Cookie", ".dit.session=chunks-2; path=/");
        missing.Headers.Add("Set-Cookie", $".dit.sessionC1={protectedTicket[..half]}; path=/");
        using HttpResponseMessage deleted = new();
        deleted.Headers.Add("Set-Cookie", ".dit.session=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/");

        Assert.Equal("99", SessionCookieReader.Read(provider, chunked)?.FindFirst("telegram_user_id")?.Value);
        Assert.Null(SessionCookieReader.Read(provider, missing));
        Assert.Null(SessionCookieReader.Read(provider, deleted));
    }

    [Fact]
    public async Task Driver_FollowsChallengeThroughFakeAuthToCallback()
    {
        await using FakeTelegramOidcServer server = await FakeTelegramOidcServer.StartAsync(ClientId);
        await using WebApplication app = await StartMiniAppAsync(server);
        using HttpClient client = app.GetTestClient();
        client.BaseAddress = new Uri("https://localhost");

        using HttpResponseMessage response = await TelegramSignInDriver.SignInAsync(
            client, server, new FakeTelegramIdentity(55, "Grace", "grace"), "/en/dotnet/profile", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("55|/en/dotnet/profile", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SessionCookieReader_UnprotectsTheCookieFromSetCookie()
    {
        await using WebApplication app = await StartCookieAppAsync();
        using HttpClient client = app.GetTestClient();
        client.BaseAddress = new Uri("https://localhost");

        using HttpResponseMessage response = await client.GetAsync("/login", TestContext.Current.CancellationToken);

        ClaimsPrincipal? principal = SessionCookieReader.Read(app.Services, response);
        AuthenticationProperties? properties = SessionCookieReader.ReadProperties(app.Services, response);
        Assert.NotNull(principal);
        Assert.Equal("77", principal.FindFirst("telegram_user_id")?.Value);
        Assert.True(properties?.IsPersistent);
    }

    [Fact]
    public void SessionCookieReader_ReturnsNullWhenNoSessionCookieWasIssued()
    {
        using HttpResponseMessage response = new(HttpStatusCode.OK);
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();

        Assert.Null(SessionCookieReader.Read(services, response));
    }

    [Fact]
#pragma warning disable CA1848 // The test exercises the plain logging extension methods on purpose.
    public void CapturingLogger_RecordsStateValuesScopesAndExceptions()
    {
        CapturingLoggerProvider provider = new();
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(provider).SetMinimumLevel(LogLevel.Trace));
        ILogger logger = factory.CreateLogger("Sample.Category");

        using (logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = "abc" }))
        using (logger.BeginScope("outer {Name}", "scope"))
        {
            logger.LogWarning(new InvalidOperationException("kaput"), "Denied {TelegramUserId} for {Reason}", 99L, "gate");
        }

        logger.LogInformation("plain");

        Assert.Equal(2, provider.Entries.Count);
        CapturedLogEntry first = provider.Entries[0];
        Assert.Equal("Sample.Category", first.Category);
        Assert.Equal(LogLevel.Warning, first.Level);
        Assert.Equal("Denied 99 for gate", first.Message);
        Assert.Equal(99L, first.State["TelegramUserId"]);
        Assert.Equal("gate", first.State["Reason"]);
        Assert.Contains("InvalidOperationException", first.Exception, StringComparison.Ordinal);
        Assert.Contains("kaput", first.Exception, StringComparison.Ordinal);
        Assert.Equal(2, first.Scopes.Count);
        Assert.Equal("abc", first.Scopes.Single(s => s.Values.ContainsKey("RequestId")).Values["RequestId"]);
        Assert.Equal("scope", first.Scopes.Single(s => s.Values.ContainsKey("Name")).Values["Name"]);
        Assert.Contains("outer scope", first.Scopes.Select(s => s.Text));
        Assert.Null(provider.Entries[1].Exception);
        Assert.Empty(provider.Entries[1].Scopes);

        provider.Clear();
        Assert.Empty(provider.Entries);
    }
#pragma warning restore CA1848

    private static HttpClient CreateHttpClient() =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false });

    private static string PkceChallenge(string verifier) =>
        Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static async Task<Uri> AuthorizeAsync(
        HttpClient http, FakeTelegramOidcServer server, string nonce, string state, string? codeChallenge = null)
    {
        Dictionary<string, string?> query = new()
        {
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["response_type"] = "code",
            ["scope"] = "openid profile",
            ["nonce"] = nonce,
            ["state"] = state,
        };
        query["code_challenge"] = codeChallenge ?? PkceChallenge(DefaultVerifier);
        query["code_challenge_method"] = "S256";

        Uri authorize = new(QueryHelpers.AddQueryString(new Uri(server.MetadataAddress, "/auth").ToString(), query));
        using HttpResponseMessage response = await http.GetAsync(authorize, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Uri location = response.Headers.Location!;
        Assert.StartsWith(RedirectUri + "?", location.ToString(), StringComparison.Ordinal);
        return location;
    }

    private static async Task<HttpResponseMessage> ExchangeAsync(
        HttpClient http,
        FakeTelegramOidcServer server,
        string code,
        string? verifier = null,
        string? secret = null,
        string grantType = "authorization_code")
    {
        Dictionary<string, string> form = new()
        {
            ["grant_type"] = grantType,
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId,
            ["client_secret"] = secret ?? server.ClientSecret,
            ["code_verifier"] = verifier ?? DefaultVerifier,
        };

        using FormUrlEncodedContent content = new(form);
        return await http.PostAsync(new Uri(server.MetadataAddress, "/token"), content, TestContext.Current.CancellationToken);
    }

    /// <summary>A host with just enough OIDC-shaped endpoints to exercise the driver without the real library.</summary>
    private static async Task<WebApplication> StartMiniAppAsync(FakeTelegramOidcServer server)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        WebApplication app = builder.Build();
        app.MapGet("/signin-telegram/challenge", (string returnUrl) =>
        {
            string url = QueryHelpers.AddQueryString(new Uri(server.MetadataAddress, "/auth").ToString(), new Dictionary<string, string?>
            {
                ["client_id"] = ClientId,
                ["redirect_uri"] = RedirectUri,
                ["response_type"] = "code",
                ["nonce"] = "mini-nonce",
                ["state"] = returnUrl,
                ["code_challenge"] = PkceChallenge(DefaultVerifier),
                ["code_challenge_method"] = "S256",
            });
            return Results.Redirect(url);
        });
        app.MapGet("/signin-telegram", async (string code, string state) =>
        {
            using HttpClient http = CreateHttpClient();
            using HttpResponseMessage tokenResponse = await ExchangeAsync(http, server, code);
            tokenResponse.EnsureSuccessStatusCode();
            using JsonDocument tokens = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            JsonWebToken token = new(tokens.RootElement.GetProperty("id_token").GetString()!);
            return Results.Text($"{token.GetPayloadValue<long>("id")}|{state}");
        });
        await app.StartAsync();
        return app;
    }

    private static async Task<WebApplication> StartCookieAppAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("TelegramCookie")
            .AddCookie("TelegramCookie", options => options.Cookie.Name = ".dit.session");
        WebApplication app = builder.Build();
        app.MapGet("/login", async (HttpContext http) =>
        {
            ClaimsIdentity identity = new([new Claim("telegram_user_id", "77")], "TelegramCookie");
            await http.SignInAsync("TelegramCookie", new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });
            return Results.Ok();
        });
        await app.StartAsync();
        return app;
    }
}
