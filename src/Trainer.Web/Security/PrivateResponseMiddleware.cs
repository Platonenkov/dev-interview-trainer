using Microsoft.Net.Http.Headers;

namespace Trainer.Web.Security;

/// <summary>
/// Marks a signed-in Razor component page <c>Cache-Control: private, no-store</c>, so a shared browser's Back button
/// after sign-out shows no personal page. Anonymous pages keep the shared caching of the output cache, and static
/// assets are not touched, so they keep their immutable caching for everyone.
/// </summary>
/// <remarks>
/// <c>OnStarting</c> callbacks run last-registered-first, and the cookie handler registers its own when authentication
/// first runs, writing <c>no-cache, no-store</c> when it reissues the cookie. So this middleware must sit before
/// <c>UseAuthentication</c> (after routing, which supplies the endpoint), and it decides at response start, when the
/// user is known.
/// </remarks>
public sealed class PrivateResponseMiddleware(RequestDelegate next)
{
    public const string CacheControlValue = "private, no-store";

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (RazorPage.IsMatched(context))
        {
            context.Response.OnStarting(static state =>
            {
                HttpContext http = (HttpContext)state;
                if (RazorPage.IsAuthenticated(http))
                {
                    http.Response.Headers[HeaderNames.CacheControl] = CacheControlValue;
                }

                return Task.CompletedTask;
            }, context);
        }

        return next(context);
    }
}
