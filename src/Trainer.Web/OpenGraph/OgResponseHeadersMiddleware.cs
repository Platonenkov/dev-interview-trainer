using Microsoft.Net.Http.Headers;

namespace Trainer.Web.OpenGraph;

/// <summary>
/// Fixes the headers of every <c>/og</c> response at the last moment. The cookie handler registers its own start-of-response
/// callback when authentication runs, and for a signed-in visitor it may add <c>Set-Cookie</c> (sliding renewal) and
/// replace <c>Cache-Control</c> with no-store. A public, immutable image must carry neither a cookie nor a different policy,
/// or a shared cache in front of the site could store one visitor's cookie. Callbacks run last-registered-first, so this
/// middleware must run before <c>UseAuthentication</c>.
/// </summary>
public sealed class OgResponseHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.OnStarting(static state =>
        {
            HttpContext http = (HttpContext)state;
            http.Response.Headers.Remove(HeaderNames.SetCookie);
            string? policy = http.Response.StatusCode switch
            {
                StatusCodes.Status200OK => OgImageEndpoints.ImmutableCacheControl,
                StatusCodes.Status301MovedPermanently => OgImageEndpoints.RedirectCacheControl,
                StatusCodes.Status503ServiceUnavailable => OgImageEndpoints.BusyCacheControl,
                _ => null,
            };
            if (policy is not null)
            {
                http.Response.Headers[HeaderNames.CacheControl] = policy;
            }

            return Task.CompletedTask;
        }, context);

        return next(context);
    }
}
