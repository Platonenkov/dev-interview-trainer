namespace Trainer.Web.Auth;

/// <summary>Scheme names, paths and lifetimes of Telegram sign-in (spec sections 4 and 8.8).</summary>
public static class TelegramAuth
{
    public const string CookieScheme = "TelegramCookie";
    public const string OidcScheme = "TelegramOidc";

    /// <summary>The OIDC callback; the library maps <c>{CallbackPath}/challenge</c> and <c>{CallbackPath}/logout</c> under it.</summary>
    public const string CallbackPath = "/signin-telegram";

    /// <summary>Culture-neutral sign-in path; <see cref="SignInRedirectEndpoint"/> sends it on to the culture's sign-in page.</summary>
    public const string LoginPath = "/signin";

    public const string AccessDeniedPath = "/signin?denied=1";

    /// <summary>Session cookie lifetime: 30 days, sliding.</summary>
    public const int CookieTtlHours = 720;

    /// <summary>How often a live session is re-checked against the database (Task 6).</summary>
    public static readonly TimeSpan RevalidationInterval = TimeSpan.FromMinutes(5);
}
