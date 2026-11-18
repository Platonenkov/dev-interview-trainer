using Microsoft.Extensions.Options;
using Trainer.Web.Islands;

namespace Trainer.Web.Startup;

/// <summary>
/// Adds the security headers to every response. The CSP has no <c>unsafe-inline</c>: the only inline script allowed is the
/// theme script, by hash. There are no third-party origins, so fonts and styles must be served from this site.
/// Island pages (<see cref="InteractiveIslandPageAttribute"/>) alone add <c>'wasm-unsafe-eval'</c>, which the
/// WebAssembly runtime needs to compile its modules, the request's <see cref="CspNonce"/> for the framework's import map
/// (ADR 0012), and allow a same-origin <c>&lt;base&gt;</c>; no other response may run WebAssembly or carry a nonce.
/// The static edition sends no security header at all: its pages carry <see cref="StaticCsp"/> as a meta element, so a
/// local static run behaves like GitHub Pages (ADR 0024).
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, IOptions<AppOptions> appOptions)
{
    private readonly bool _isStatic = appOptions.Value.Edition == SiteEdition.Static;

    public static readonly string ContentSecurityPolicy = Policy(scriptSources: $"'self' {ThemeScript.CspSource}", baseUri: "'none'");

    /// <summary>
    /// The policy of island pages: <see cref="ContentSecurityPolicy"/> plus WebAssembly compilation, the import map
    /// carrying <paramref name="nonce"/> and a same-origin <c>&lt;base&gt;</c>, which the runtime needs to resolve its
    /// <c>_framework/</c> URLs from any page path.
    /// </summary>
    public static string IslandContentSecurityPolicy(string nonce) =>
        Policy(scriptSources: $"'self' 'wasm-unsafe-eval' 'nonce-{nonce}' {ThemeScript.CspSource}", baseUri: "'self'");

    public Task InvokeAsync(HttpContext context)
    {
        if (_isStatic)
        {
            return next(context);
        }

        IHeaderDictionary headers = context.Response.Headers;
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

        // This middleware runs before routing, so the endpoint is known only when the response starts. A status page
        // re-executed for an island path is matched to the not-found page and keeps the default policy.
        context.Response.OnStarting(static state =>
        {
            HttpContext http = (HttpContext)state;
            if (InteractiveIslandPageAttribute.IsMatched(http))
            {
                // App.razor renders the import map with the same scoped nonce.
                http.Response.Headers.ContentSecurityPolicy = IslandContentSecurityPolicy(http.RequestServices.GetRequiredService<CspNonce>().Value);
            }

            return Task.CompletedTask;
        }, context);
        return next(context);
    }

    private static string Policy(string scriptSources, string baseUri) =>
        "default-src 'self'; " +
        $"script-src {scriptSources}; " +
        "style-src 'self'; " +
        "img-src 'self' https: data:; " +
        "font-src 'self'; " +
        "frame-ancestors 'none'; " +
        $"base-uri {baseUri}; " +
        "form-action 'self'";
}
