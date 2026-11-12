namespace Trainer.Web.Startup;

/// <summary>
/// Adds the security headers to every response. The CSP has no <c>unsafe-inline</c>: the only inline script allowed is the
/// theme script, by hash. There are no third-party origins, so fonts and styles must be served from this site.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public static readonly string ContentSecurityPolicy =
        "default-src 'self'; " +
        $"script-src 'self' {ThemeScript.CspSource}; " +
        "style-src 'self'; " +
        "img-src 'self' https: data:; " +
        "font-src 'self'; " +
        "frame-ancestors 'none'; " +
        "base-uri 'none'; " +
        "form-action 'self'";

    public Task InvokeAsync(HttpContext context)
    {
        IHeaderDictionary headers = context.Response.Headers;
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        return next(context);
    }
}
