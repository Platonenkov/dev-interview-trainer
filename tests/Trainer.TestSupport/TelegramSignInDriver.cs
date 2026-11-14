using System.Net;

namespace Trainer.TestSupport;

/// <summary>Drives the browser side of the Telegram OIDC flow against a <see cref="FakeTelegramOidcServer"/>.</summary>
public static class TelegramSignInDriver
{
    private const string ChallengePath = "/signin-telegram/challenge";

    /// <summary>
    /// Runs challenge, the fake authorization endpoint and the callback, and returns the callback response.
    /// </summary>
    /// <param name="app">
    /// The app client. It must not follow redirects (the challenge redirect points at the fake server) and must keep
    /// cookies, because the correlation, nonce and state cookies set on the challenge are needed by the callback.
    /// </param>
    /// <param name="telegram">The fake server the app's OIDC handler was configured with.</param>
    /// <param name="who">The Telegram account that signs in.</param>
    /// <param name="returnUrl">A local path the app should send the user to afterwards.</param>
    /// <param name="ct">Cancels the three requests.</param>
    public static async Task<HttpResponseMessage> SignInAsync(
        HttpClient app,
        FakeTelegramOidcServer telegram,
        FakeTelegramIdentity who,
        string returnUrl,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(telegram);
        ArgumentNullException.ThrowIfNull(who);

        telegram.NextIdentity = who;
        Uri authorizeUri = await ChallengeAsync(app, returnUrl, ct);
        Uri callbackUri = await AuthorizeAsync(authorizeUri, ct);
        return await app.GetAsync(callbackUri.PathAndQuery, ct);
    }

    private static async Task<Uri> ChallengeAsync(HttpClient app, string returnUrl, CancellationToken ct)
    {
        using HttpResponseMessage challenge = await app.GetAsync($"{ChallengePath}?returnUrl={Uri.EscapeDataString(returnUrl)}", ct);
        return RequireRedirect(challenge, "challenge");
    }

    private static async Task<Uri> AuthorizeAsync(Uri authorizeUri, CancellationToken ct)
    {
        // A separate client: the browser sends no app cookies to Telegram, and the app client must not see its cookies.
        using HttpClient telegram = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
        using HttpResponseMessage authorize = await telegram.GetAsync(authorizeUri, ct);
        return RequireRedirect(authorize, "authorization");
    }

    private static Uri RequireRedirect(HttpResponseMessage response, string step)
    {
        if (response.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.RedirectKeepVerb)
            || response.Headers.Location is not { IsAbsoluteUri: true } location)
        {
            throw new InvalidOperationException(
                $"The {step} step should answer with an absolute redirect but answered {(int)response.StatusCode}.");
        }

        return location;
    }
}
