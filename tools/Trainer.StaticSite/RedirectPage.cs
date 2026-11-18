using System.Net;

namespace Trainer.StaticSite;

/// <summary>
/// The static stand-in for an HTTP 301 (Pages cannot send one): a small page that moves the visitor with a meta refresh and
/// tells crawlers where the content lives. It carries the same meta policy, referrer and build id as an app page, so the
/// verifier holds it to the same rules; it has no script, hence no recovery script either.
/// </summary>
public static class RedirectPage
{
    /// <param name="target">The absolute http(s) URL of the page the old URL moved to; the page links to its path.</param>
    /// <param name="csp">The policy for a public page, from the export manifest.</param>
    /// <param name="buildId">The deployment identity written into <c>dit-build</c>.</param>
    /// <exception cref="ArgumentException">The target is not an absolute http(s) URL, or a value is empty or has control characters.</exception>
    public static string Html(string target, string csp, string buildId)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(csp);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildId);
        if (target.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))
            || !Uri.TryCreate(target, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("The redirect target must be an absolute http(s) URL without whitespace.", nameof(target));
        }

        if (csp.Any(char.IsControl) || buildId.Any(char.IsControl))
        {
            throw new ArgumentException("The policy and the build id must not contain control characters.");
        }

        // The canonical link is absolute by convention; the refresh and the visible link stay on whatever origin serves the page,
        // so a preview of the export does not jump to the public site.
        string canonical = WebUtility.HtmlEncode(uri.AbsoluteUri);
        string url = WebUtility.HtmlEncode(uri.PathAndQuery);
        return $"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta http-equiv="Content-Security-Policy" content="{WebUtility.HtmlEncode(csp)}">
            <meta charset="utf-8">
            <meta name="referrer" content="strict-origin-when-cross-origin">
            <meta name="dit-build" content="{WebUtility.HtmlEncode(buildId)}">
            <meta http-equiv="refresh" content="0; url={url}">
            <meta name="robots" content="noindex">
            <link rel="canonical" href="{canonical}">
            <title>Redirecting</title>
            </head>
            <body>
            <p>This page has moved: <a href="{url}">{url}</a></p>
            </body>
            </html>

            """.ReplaceLineEndings("\n");
    }
}
