using Microsoft.Net.Http.Headers;
using Trainer.Domain.Keys;

namespace Trainer.Web.Routing;

/// <summary><c>GET /</c>: redirects to the culture home page chosen from the <c>lang</c> cookie or <c>Accept-Language</c>.</summary>
public static class RootRedirectEndpoint
{
    public const string CookieName = "lang";

    public static IEndpointConventionBuilder Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapGet("/", Handle);
    }

    public static CultureKey ResolveCulture(string? cookie, IReadOnlyList<string> acceptLanguageByPreference)
    {
        if (CultureKey.TryCreate(cookie, out CultureKey fromCookie))
        {
            return fromCookie;
        }

        foreach (string tag in acceptLanguageByPreference)
        {
            int dash = tag.IndexOf('-', StringComparison.Ordinal);
            string primary = (dash < 0 ? tag : tag[..dash]).ToLowerInvariant();
            if (CultureKey.TryCreate(primary, out CultureKey match))
            {
                return match;
            }
        }

        return CultureKey.Default;
    }

    private static IResult Handle(HttpContext context)
    {
        // The target depends on two request inputs, so shared caches must not reuse the redirect across them.
        context.Response.Headers.Vary = "Accept-Language, Cookie";

        context.Request.Cookies.TryGetValue(CookieName, out string? cookie);
        CultureKey culture = ResolveCulture(cookie, AcceptLanguageByPreference(context.Request));
        return Results.Redirect("/" + culture.Value, permanent: false);
    }

    /// <summary>The <c>Accept-Language</c> tags of <paramref name="request"/>, most preferred first, without <c>*</c> and q=0.</summary>
    public static IReadOnlyList<string> AcceptLanguageByPreference(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.GetTypedHeaders().AcceptLanguage
            .Where(l => (l.Quality is null or > 0) && l.Value.HasValue && l.Value != "*")
            .OrderByDescending(l => l.Quality ?? 1.0)
            .Select(l => l.Value.Value!)
            .ToList();
    }
}
