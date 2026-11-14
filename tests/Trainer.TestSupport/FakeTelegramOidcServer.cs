using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Trainer.TestSupport;

/// <summary>
/// A stand-in for oauth.telegram.org on a real loopback Kestrel port, so the app's genuine OpenID Connect handler
/// runs its full discovery, authorization-code, PKCE, token and JWKS flow without Telegram. The authorization
/// endpoint answers at once with a redirect back to the client, as Telegram does after the user consents. It insists
/// on what the app's wiring must provide: an S256 PKCE challenge, the authorization-code grant, and the client secret.
/// </summary>
/// <remarks>
/// Token times use the real clock because the OIDC handler validates lifetimes with the real clock; a test that uses
/// <c>FakeTimeProvider</c> must start it at the real now.
/// </remarks>
public sealed class FakeTelegramOidcServer : IAsyncDisposable
{
    private const string SigningKeyId = "fake-telegram-kid";
    private const string AccessToken = "fake-telegram-access-token";
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);
    private static readonly string[] ResponseTypes = ["code"];
    private static readonly string[] SubjectTypes = ["public"];
    private static readonly string[] SigningAlgorithms = ["RS256"];
    private static readonly string[] CodeChallengeMethods = ["S256"];
    private static readonly string[] TokenAuthMethods = ["client_secret_post", "client_secret_basic"];

    private readonly string _clientId;
    private readonly byte[] _clientSecret = Encoding.ASCII.GetBytes(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));
    private readonly RSA _signingKey = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, PendingCode> _codes = new(StringComparer.Ordinal);
    private readonly WebApplication _app;
    private FakeTelegramIdentity _nextIdentity = new(1001, "Test User", "test_user");
    private FailureMode? _failure;

    private FakeTelegramOidcServer(string clientId)
    {
        _clientId = clientId;
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        MapEndpoints();
    }

    /// <summary>The client id the server accepts; the host under test must be configured with the same one.</summary>
    public string ClientId => _clientId;

    /// <summary>
    /// The client secret the token endpoint requires, generated per server. The host under test is configured with it
    /// (<see cref="TrainerWebFactory"/> does), so a wrongly wired secret fails the code exchange.
    /// </summary>
    public string ClientSecret => Encoding.ASCII.GetString(_clientSecret);

    /// <summary>Origin of the server; also the <c>iss</c> of every token.</summary>
    public string Issuer { get; private set; } = string.Empty;

    /// <summary>The discovery document address, to be used as the OIDC <c>MetadataAddress</c>.</summary>
    public Uri MetadataAddress { get; private set; } = null!;

    /// <summary>The account the next authorization request signs in; it is captured when <c>/auth</c> issues the code.</summary>
    public FakeTelegramIdentity NextIdentity
    {
        get => Volatile.Read(ref _nextIdentity);
        set => Volatile.Write(ref _nextIdentity, value);
    }

    /// <summary>When true, the next authorization request is answered with <c>error=access_denied</c>; consumed by that request.</summary>
    public bool FailNext
    {
        get => Volatile.Read(ref _failure) is not null;
        set => Volatile.Write(ref _failure, value ? new FailureMode("access_denied", null) : null);
    }

    public static async Task<FakeTelegramOidcServer> StartAsync(string clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        FakeTelegramOidcServer server = new(clientId);
        try
        {
            await server._app.StartAsync();
            string address = server._app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            server.Issuer = address.TrimEnd('/');
            server.MetadataAddress = new Uri(server.Issuer + "/.well-known/openid-configuration");
            return server;
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Makes the next authorization request answer <c>error=server_error</c> with the given description, which the
    /// OIDC handler turns into a remote failure; consumed by that request.
    /// </summary>
    public void FailNextWith(string description)
    {
        ArgumentException.ThrowIfNullOrEmpty(description);
        Volatile.Write(ref _failure, new FailureMode("server_error", description));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        _signingKey.Dispose();
    }

    private void MapEndpoints()
    {
        _app.MapGet("/.well-known/openid-configuration", () => Results.Json(new Dictionary<string, object?>
        {
            ["issuer"] = Issuer,
            ["authorization_endpoint"] = Issuer + "/auth",
            ["token_endpoint"] = Issuer + "/token",
            ["jwks_uri"] = Issuer + "/jwks",
            ["response_types_supported"] = ResponseTypes,
            ["subject_types_supported"] = SubjectTypes,
            ["id_token_signing_alg_values_supported"] = SigningAlgorithms,
            ["code_challenge_methods_supported"] = CodeChallengeMethods,
            ["token_endpoint_auth_methods_supported"] = TokenAuthMethods,
        }));
        _app.MapGet("/jwks", () => Results.Json(BuildJwks()));
        _app.MapGet("/auth", Authorize);
        _app.MapPost("/token", ExchangeCodeAsync);
    }

    private object BuildJwks()
    {
        RSAParameters parameters = _signingKey.ExportParameters(includePrivateParameters: false);
        return new
        {
            keys = new[]
            {
                new Dictionary<string, string>
                {
                    ["kty"] = "RSA",
                    ["use"] = "sig",
                    ["alg"] = "RS256",
                    ["kid"] = SigningKeyId,
                    ["n"] = Base64UrlEncoder.Encode(parameters.Modulus!),
                    ["e"] = Base64UrlEncoder.Encode(parameters.Exponent!),
                },
            },
        };
    }

    private IResult Authorize(HttpRequest request)
    {
        string? redirectUri = request.Query["redirect_uri"];
        string state = request.Query["state"].ToString();
        if (string.IsNullOrEmpty(redirectUri) || !Uri.TryCreate(redirectUri, UriKind.Absolute, out _))
        {
            return Results.BadRequest("redirect_uri is required");
        }

        string? requestedClient = request.Query["client_id"];
        if (!string.Equals(requestedClient, _clientId, StringComparison.Ordinal))
        {
            return Results.BadRequest("unknown client_id");
        }

        // The library always sends a challenge; requiring one makes a regression that disables PKCE visible.
        if (request.Query["code_challenge"].ToString().Length == 0
            || !string.Equals(request.Query["code_challenge_method"], "S256", StringComparison.Ordinal))
        {
            return Results.BadRequest("an S256 code_challenge is required");
        }

        FailureMode? failure = Interlocked.Exchange(ref _failure, null);
        if (failure is not null)
        {
            Dictionary<string, string?> error = new() { ["error"] = failure.Error };
            if (failure.Description is not null)
            {
                error["error_description"] = failure.Description;
            }

            error["state"] = state;
            return Results.Redirect(QueryHelpers.AddQueryString(redirectUri, error));
        }

        string code = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
        _codes[code] = new PendingCode(
            request.Query["nonce"].ToString(),
            redirectUri,
            request.Query["code_challenge"].ToString(),
            NextIdentity);
        return Results.Redirect(QueryHelpers.AddQueryString(redirectUri, new Dictionary<string, string?>
        {
            ["code"] = code,
            ["state"] = state,
        }));
    }

    private async Task<IResult> ExchangeCodeAsync(HttpRequest request)
    {
        if (!request.HasFormContentType)
        {
            return OAuthError("invalid_request");
        }

        IFormCollection form = await request.ReadFormAsync(request.HttpContext.RequestAborted);
        string code = form["code"].ToString();
        if (!string.Equals(form["grant_type"].ToString(), "authorization_code", StringComparison.Ordinal))
        {
            return OAuthError("unsupported_grant_type");
        }

        if (!_codes.TryRemove(code, out PendingCode? pending))
        {
            return OAuthError("invalid_grant");
        }

        if (!string.Equals(form["redirect_uri"].ToString(), pending.RedirectUri, StringComparison.Ordinal)
            || !ClientMatches(request, form)
            || !VerifierMatches(pending.CodeChallenge, form["code_verifier"].ToString()))
        {
            return OAuthError("invalid_grant");
        }

        return Results.Json(new Dictionary<string, object?>
        {
            ["token_type"] = "Bearer",
            ["access_token"] = AccessToken,
            ["expires_in"] = 3600,
            ["id_token"] = MintIdToken(pending),
        });
    }

    private bool ClientMatches(HttpRequest request, IFormCollection form)
    {
        string? presented = form["client_id"];
        if (string.IsNullOrEmpty(presented)
            && request.Headers.Authorization.ToString() is { Length: > 0 } header
            && header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..]));
            presented = Uri.UnescapeDataString(decoded.Split(':', 2)[0]);
        }

        return string.Equals(presented, _clientId, StringComparison.Ordinal) && SecretMatches(request, form);
    }

    // The secret arrives in the body (client_secret_post) or as the Basic password (client_secret_basic).
    private bool SecretMatches(HttpRequest request, IFormCollection form)
    {
        string? presented = form["client_secret"];
        if (string.IsNullOrEmpty(presented)
            && request.Headers.Authorization.ToString() is { Length: > 0 } header
            && header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..]));
            string[] parts = decoded.Split(':', 2);
            presented = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : null;
        }

        return presented is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _clientSecret);
    }

    private static bool VerifierMatches(string codeChallenge, string verifier)
    {
        string computed = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return string.Equals(computed, codeChallenge, StringComparison.Ordinal);
    }

    private static IResult OAuthError(string error) =>
        Results.Json(new { error }, statusCode: StatusCodes.Status400BadRequest);

    private string MintIdToken(PendingCode pending)
    {
        DateTime now = TimeProvider.System.GetUtcNow().UtcDateTime;
        FakeTelegramIdentity identity = pending.Identity;
        Dictionary<string, object> claims = new()
        {
            ["sub"] = "tg-sub-" + identity.Id.ToString(CultureInfo.InvariantCulture),
            ["id"] = identity.Id,
            ["nonce"] = pending.Nonce,
            ["at_hash"] = ComputeAtHash(),
        };
        if (identity.Name is not null)
        {
            claims["name"] = identity.Name;
        }

        if (identity.Username is not null)
        {
            claims["preferred_username"] = identity.Username;
        }

        SecurityTokenDescriptor descriptor = new()
        {
            Issuer = Issuer,
            Audience = _clientId,
            IssuedAt = now,
            Expires = now + TokenLifetime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(_signingKey) { KeyId = SigningKeyId },
                SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    // RS256 hashes with SHA-256; at_hash is the base64url of the left half of that digest.
    private static string ComputeAtHash()
    {
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(AccessToken));
        return Base64UrlEncoder.Encode(hash, 0, hash.Length / 2);
    }

    private sealed record PendingCode(string Nonce, string RedirectUri, string CodeChallenge, FakeTelegramIdentity Identity);

    private sealed record FailureMode(string Error, string? Description);
}
