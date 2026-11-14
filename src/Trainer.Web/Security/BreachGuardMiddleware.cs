using Microsoft.AspNetCore.Http.Features;
using Trainer.Web.Islands;

namespace Trainer.Web.Security;

/// <summary>
/// Keeps responses that carry an antiforgery token out of response compression (BREACH: compressing a secret next to
/// attacker-influenced text leaks it through the response size). It acts on a signed-in Razor component page, whose
/// layout renders the logout form's token, and, as the one named exception to "pages only", on
/// <see cref="AntiforgeryPath"/> (any letter case, with or without a trailing slash), which returns a token as JSON.
/// Island pages carry one even for anonymous visitors. Other anonymous pages carry no token and stay compressed; so does
/// other <c>/api</c> JSON, which carries none.
/// </summary>
/// <remarks>
/// Place it after routing (it reads the matched endpoint) and authentication. The compression middleware installs
/// its feature only when the client accepts an encoding, so a missing feature means nothing will be compressed.
/// </remarks>
public sealed class BreachGuardMiddleware(RequestDelegate next)
{
    /// <summary>The endpoint that hands out the antiforgery token (spec 8.8).</summary>
    public const string AntiforgeryPath = "/api/antiforgery";

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (CarriesToken(context) && context.Features.Get<IHttpsCompressionFeature>() is { } feature)
        {
            feature.Mode = HttpsCompressionMode.DoNotCompress;
        }

        return next(context);
    }

    // An island page also carries a token when anonymous: the framework persists the antiforgery state for every
    // WebAssembly component it renders.
    private static bool CarriesToken(HttpContext context) =>
        (RazorPage.IsMatched(context) && (RazorPage.IsAuthenticated(context) || InteractiveIslandPageAttribute.IsMatched(context)))
        || IsAntiforgeryEndpoint(context.Request.Path);

    // Endpoint routing ignores case and a single trailing slash, so this must too, or a differently spelled request
    // would reach the endpoint with compression on.
    private static bool IsAntiforgeryEndpoint(PathString path) =>
        path.StartsWithSegments(AntiforgeryPath, StringComparison.OrdinalIgnoreCase, out PathString rest)
        && (!rest.HasValue || string.Equals(rest.Value, "/", StringComparison.Ordinal));
}
