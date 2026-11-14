using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Trainer.Application.Users;
using Trainer.Domain.Keys;
using Trainer.Domain.Users;
using Trainer.Web.Routing;
using Trainer.Web.Security;

namespace Trainer.Web.Auth;

/// <summary>
/// Session cookie events: persistence and the stored language preference at sign-in, revalidation of a live session
/// against the database, the login and denied redirects.
/// </summary>
public sealed class TrainerCookieEvents(
    IUserAccountService users, UserClaimsFactory claims, TimeProvider time, ILogger<TrainerCookieEvents> logger) : CookieAuthenticationEvents
{
    /// <summary>
    /// Authentication property holding the time of the last database check. The ticket's own <c>IssuedUtc</c> cannot
    /// serve: the sliding renewal moves it forward on every renewal, which would postpone the check forever.
    /// </summary>
    public const string LastCheckedKey = ".dit.checked";

    private static readonly Action<ILogger, string, Exception?> RevalidationFailed = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(1, nameof(RevalidationFailed)), "Session revalidation failed ({ExceptionType}); the request is treated as anonymous.");

    private static readonly HashSet<string> RefreshedClaimTypes =
        [TrainerClaimTypes.UserId, TrainerClaimTypes.SecurityStamp, ClaimTypes.Role];

    /// <summary>
    /// Runs inside the authenticated OIDC callback, the only place the preference is read or written. An explicit
    /// <c>lang</c> choice on this device is stored; a device without one gets the stored choice back. The root
    /// redirect order (cookie, then <c>Accept-Language</c>) is untouched.
    /// </summary>
    public override async Task SigningIn(CookieSigningInContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The library's challenge never sets IsPersistent; without it the 30-day ticket would sit in a session cookie.
        context.Properties.IsPersistent = true;

        if (context.Principal?.FindFirst(TrainerClaimTypes.UserId)?.Value is not { } rawUserId
            || !UserId.TryCreate(rawUserId, out UserId userId)
            || context.Principal.GetTelegramUserId() is not { } telegramUserId)
        {
            return;
        }

        HttpContext http = context.HttpContext;
        CancellationToken ct = http.RequestAborted;
        http.Request.Cookies.TryGetValue(RootRedirectEndpoint.CookieName, out string? cookie);
        UserAccess? access = await users.GetAccessAsync(telegramUserId, ct);
        if (CultureKey.TryCreate(cookie, out CultureKey chosen))
        {
            if (access?.PreferredCulture != chosen)
            {
                await users.SetPreferredCultureAsync(userId, chosen, ct);
            }
        }
        else if (cookie is null && access?.PreferredCulture is { } stored)
        {
            http.Response.Cookies.Append(RootRedirectEndpoint.CookieName, stored.Value, LanguageEndpoint.CreateCookieOptions(http.Request));
        }
    }

    /// <summary>
    /// At most every <see cref="TelegramAuth.RevalidationInterval"/> a session is checked against the database: a
    /// missing or blocked user ends the session, a changed security stamp or role set is written into a refreshed
    /// cookie. Inside the interval the cookie alone decides and the database is not touched.
    /// </summary>
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        DateTimeOffset now = time.GetUtcNow();
        // A last check in the future (clock stepped back, another instance ahead) is not "just checked": only a time
        // inside the past interval counts, otherwise the check would be skipped until the clock caught up.
        if (LastChecked(context.Properties) is { } last && IsWithinInterval(now - last))
        {
            return;
        }

        if (context.Principal?.GetTelegramUserId() is not { } telegramUserId
            || context.Principal.Identity is not ClaimsIdentity identity)
        {
            await RejectAsync(context);
            return;
        }

        UserAccess? access;
        try
        {
            access = await users.GetAccessAsync(telegramUserId, context.HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail closed for this request without ending the session: the principal is not trusted unverified, but a
            // database blip must not sign everyone out, so the cookie stays and the next request checks again.
            RevalidationFailed(logger, ex.GetType().Name, null);
            RevalidationOutage.Mark(context.HttpContext);
            context.RejectPrincipal();
            return;
        }

        if (access is null || access.IsBlocked)
        {
            await RejectAsync(context);
            return;
        }

        if (IsStale(identity, access))
        {
            context.ReplacePrincipal(new ClaimsPrincipal(Refresh(identity, access)));
        }

        // Persists the check time (and any new claims) in a reissued cookie. Only pages and /api reissue it: a
        // Set-Cookie (with the handler's no-cache headers) on a fingerprinted asset would replace its immutable caching,
        // and the next page request does the refresh anyway.
        if (RazorPage.IsMatched(context.HttpContext) || IsApiRequest(context.Request))
        {
            context.Properties.Items[LastCheckedKey] = now.ToString("O", CultureInfo.InvariantCulture);
            context.ShouldRenew = true;
        }
    }

    /// <summary>
    /// <c>/api</c> callers are scripts: they get a bare 401 (the JSON body is written by the API layer), never a
    /// redirect to the sign-in page. A request that is anonymous only because revalidation hit a database failure gets a
    /// bare 503 instead (<see cref="RevalidationOutage"/>).
    /// </summary>
    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (IsApiRequest(context.Request))
        {
            context.Response.StatusCode = RevalidationOutage.IsMarked(context.HttpContext)
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        // The default writes scheme, host and path base; the redirect is same-site, so the relative form is enough and
        // does not depend on the forwarded host configuration.
        context.Response.Redirect(SameSiteTarget(context.RedirectUri));
        return Task.CompletedTask;
    }

    /// <summary>
    /// The default appends <c>?ReturnUrl=</c> to <see cref="TelegramAuth.AccessDeniedPath"/>, which already has a query,
    /// so its <c>?</c> would be escaped into the path. The return URL is the original path and query.
    /// </summary>
    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        HttpRequest request = context.Request;
        if (IsApiRequest(request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        string original = request.PathBase + request.Path + request.QueryString;
        context.Response.Redirect($"{TelegramAuth.AccessDeniedPath}&{context.Options.ReturnUrlParameter}={Uri.EscapeDataString(original)}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// The path and query of an absolute http(s) redirect URI, anything else unchanged. Only http(s) counts as absolute:
    /// on Linux <see cref="Uri.TryCreate(string, UriKind, out Uri)"/> reads a rooted path such as <c>/signin?ReturnUrl=x</c>
    /// as an implicit <c>file:</c> URI and would drop the query.
    /// </summary>
    public static string SameSiteTarget(string redirectUri) =>
        Uri.TryCreate(redirectUri, UriKind.Absolute, out Uri? absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps)
            ? absolute.PathAndQuery
            : redirectUri;

    private static bool IsWithinInterval(TimeSpan elapsed) =>
        elapsed >= TimeSpan.Zero && elapsed < TelegramAuth.RevalidationInterval;

    private static bool IsApiRequest(HttpRequest request) => request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset? LastChecked(AuthenticationProperties properties)
    {
        if (properties.Items.TryGetValue(LastCheckedKey, out string? raw)
            && DateTimeOffset.TryParseExact(raw, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
        {
            return parsed;
        }

        return properties.IssuedUtc;
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(context.Scheme.Name);
    }

    private static bool IsStale(ClaimsIdentity identity, UserAccess access)
    {
        string? stamp = identity.FindFirst(TrainerClaimTypes.SecurityStamp)?.Value;
        HashSet<string> roles = [.. identity.FindAll(ClaimTypes.Role).Select(c => c.Value)];
        return !string.Equals(stamp, access.SecurityStamp, StringComparison.Ordinal) || !roles.SetEquals(access.Roles);
    }

    /// <summary>
    /// Rebuilds the identity with the current application claims. Every other claim is kept as issued, above all
    /// <c>sub</c>: antiforgery binds its tokens to it, so a refresh must not invalidate a token already handed out.
    /// </summary>
    private ClaimsIdentity Refresh(ClaimsIdentity identity, UserAccess access)
    {
        IEnumerable<Claim> kept = identity.Claims.Where(c => !RefreshedClaimTypes.Contains(c.Type));
        return new ClaimsIdentity(kept.Concat(claims.Create(access)), identity.AuthenticationType, identity.NameClaimType, identity.RoleClaimType);
    }
}
