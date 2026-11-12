using System.Globalization;
using System.Text;
using Trainer.Domain.Keys;

namespace Trainer.Web.Routing;

/// <summary>
/// <c>GET /lang/{target}?return={path}</c>: the explicit language choice. It stores the choice in the <c>lang</c> cookie
/// and redirects back to the same page in the chosen culture. This is the only place the cookie is written: public pages
/// must stay cookie-free so the output cache can serve them, which is why the language switcher links here instead of
/// setting the cookie from a page.
/// </summary>
public static class LanguageEndpoint
{
    public const string PathPrefix = "/lang";
    public const string ReturnParameter = "return";

    public static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(365);

    // The route parameter is deliberately not called "Culture": that name makes CultureAndStackMiddleware judge the request.
    public static IEndpointConventionBuilder Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapGet(PathPrefix + "/{target}", Handle);
    }

    /// <summary>The URL the language switcher links to for <paramref name="targetPath"/> in <paramref name="target"/>.</summary>
    public static string SwitchUrl(CultureKey target, string targetPath)
    {
        ArgumentNullException.ThrowIfNull(targetPath);
        return $"{PathPrefix}/{target.Value}?{ReturnParameter}={Uri.EscapeDataString(targetPath)}";
    }

    /// <summary>
    /// The validated redirect target. Anything that is not a plain site-relative path inside <paramref name="culture"/>
    /// (another origin, a protocol-relative or backslash form, control characters, another culture) maps to the culture home.
    /// </summary>
    public static string ResolveReturnPath(string? candidate, CultureKey culture)
    {
        string home = "/" + culture.Value;
        if (string.IsNullOrEmpty(candidate)
            || candidate.Contains("//", StringComparison.Ordinal)
            || candidate.Contains('\\')
            || candidate.Any(char.IsControl))
        {
            return home;
        }

        int queryStart = candidate.IndexOf('?', StringComparison.Ordinal);
        string path = queryStart < 0 ? candidate : candidate[..queryStart];
        bool insideCulture = string.Equals(path, home, StringComparison.Ordinal)
            || path.StartsWith(home + "/", StringComparison.Ordinal);
        return insideCulture ? candidate : home;
    }

    private static IResult Handle(HttpContext context, string target)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Robots-Tag"] = "noindex";

        if (!CultureKey.TryCreate(target, out CultureKey culture))
        {
            return Results.NotFound();
        }

        context.Response.Cookies.Append(RootRedirectEndpoint.CookieName, culture.Value, new CookieOptions
        {
            Path = "/",
            MaxAge = CookieLifetime,
            SameSite = SameSiteMode.Lax,
            HttpOnly = true,
            Secure = context.Request.IsHttps,
        });

        string destination = ResolveReturnPath(context.Request.Query[ReturnParameter].ToString(), culture);
        return Results.Redirect(ToLocation(destination), permanent: false);
    }

    /// <summary>
    /// The query binder hands over decoded text, but a header value must be ASCII: a raw non-ASCII character in
    /// <c>Location</c> makes the server fail the response. Only the non-ASCII characters are percent-encoded (as UTF-8),
    /// so a path and query that were already valid, such as a catalog URL, stay byte for byte the same.
    /// </summary>
    public static string ToLocation(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.All(char.IsAscii))
        {
            return path;
        }

        StringBuilder builder = new(path.Length + 16);
        Span<byte> utf8 = stackalloc byte[4];
        foreach (Rune rune in path.EnumerateRunes())
        {
            if (rune.IsAscii)
            {
                builder.Append((char)rune.Value);
                continue;
            }

            int length = rune.EncodeToUtf8(utf8);
            for (int i = 0; i < length; i++)
            {
                builder.Append('%').Append(utf8[i].ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }
}
