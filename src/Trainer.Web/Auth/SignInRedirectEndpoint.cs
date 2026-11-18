using Trainer.Domain.Keys;
using Trainer.Web.Routing;
using Trainer.Web.Seo;

namespace Trainer.Web.Auth;

/// <summary>
/// <c>GET /signin</c>: the culture-neutral login path the library and the cookie handler redirect to. It forwards to the
/// sign-in page of the right culture, keeping the <c>denied</c> / <c>error</c> flags and a validated return path.
/// </summary>
public static class SignInRedirectEndpoint
{
    /// <summary>
    /// The return path parameter. Query keys match case-insensitively, so this also reads the cookie handler's
    /// <c>ReturnUrl</c>.
    /// </summary>
    public const string ReturnUrlParameter = "returnUrl";

    public const string DeniedParameter = "denied";
    public const string ErrorParameter = "error";

    public static IEndpointConventionBuilder Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapGet(TelegramAuth.LoginPath, Handle);
    }

    /// <summary>The sign-in page URL in <paramref name="culture"/> for an already validated <paramref name="returnPath"/>.</summary>
    public static string PagePath(CultureKey culture, string? returnPath, bool denied = false, bool error = false)
    {
        List<string> query = [];
        if (denied)
        {
            query.Add(DeniedParameter + "=1");
        }

        if (error)
        {
            query.Add(ErrorParameter + "=1");
        }

        if (returnPath is not null)
        {
            query.Add(ReturnUrlParameter + "=" + Uri.EscapeDataString(returnPath));
        }

        string path = $"/{culture.Value}/signin";
        return query.Count == 0 ? path : path + "?" + string.Join('&', query);
    }

    private static IResult Handle(HttpContext context, SitePaths paths)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Robots-Tag"] = "noindex";

        IQueryCollection query = context.Request.Query;
        string returnUrl = query[ReturnUrlParameter].ToString();

        CultureKey culture = CulturePath.FromPath(returnUrl) ?? CultureFromRequest(context.Request);
        string? returnPath = string.IsNullOrEmpty(returnUrl) ? null : LanguageEndpoint.ResolveReturnPath(returnUrl, culture);
        string target = PagePath(culture, returnPath, IsSet(query, DeniedParameter), IsSet(query, ErrorParameter));
        return Results.Redirect(paths.Page(target), permanent: false);
    }

    /// <summary>
    /// The redirect target after a completed sign-in: <c>/</c>, or a path inside a culture that passes
    /// <see cref="LanguageEndpoint.ResolveReturnPath"/> (an unsafe path inside a culture becomes that culture's home).
    /// Anything else, including paths with control characters, becomes <c>/</c>. The result is a valid header value:
    /// non-ASCII characters are percent-encoded (<see cref="LanguageEndpoint.ToLocation"/>), because the library
    /// writes it into <c>Location</c> as is.
    /// </summary>
    public static string ResolveReturnPathAfterSignIn(string? candidate)
    {
        if (string.Equals(candidate, "/", StringComparison.Ordinal))
        {
            return "/";
        }

        return CulturePath.FromPath(candidate) is { } culture
            ? LanguageEndpoint.ToLocation(LanguageEndpoint.ResolveReturnPath(candidate, culture))
            : "/";
    }

    /// <summary>
    /// <see cref="ResolveReturnPathAfterSignIn"/> as a location under the path base: the library writes the value into
    /// <c>Location</c> as is, so it must be the final URL. The validation is the same as without a path base.
    /// </summary>
    public static string ResolveReturnLocationAfterSignIn(string? candidate, SitePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return paths.Page(ResolveReturnPathAfterSignIn(candidate));
    }

    private static bool IsSet(IQueryCollection query, string flag) =>
        string.Equals(query[flag].ToString(), "1", StringComparison.Ordinal);

    private static CultureKey CultureFromRequest(HttpRequest request)
    {
        request.Cookies.TryGetValue(RootRedirectEndpoint.CookieName, out string? cookie);
        return RootRedirectEndpoint.ResolveCulture(cookie, RootRedirectEndpoint.AcceptLanguageByPreference(request));
    }
}
