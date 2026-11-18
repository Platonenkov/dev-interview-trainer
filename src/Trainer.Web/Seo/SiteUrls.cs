using Microsoft.Extensions.Options;
using Trainer.Web.Startup;

namespace Trainer.Web.Seo;

/// <summary>
/// Builds absolute public URLs. <c>App:PublicBaseUrl</c> wins so canonical links and sitemaps stay stable behind a
/// reverse proxy; the request origin is only a fallback for Development and Testing, where startup validation
/// allows the setting to be empty. The path of the setting is the path base and is applied by <see cref="SitePaths"/>.
/// </summary>
public sealed class SiteUrls(IOptions<AppOptions> options, SitePaths paths)
{
    /// <summary>Origin plus a page URL for a logical path and query (slashed in the static edition).</summary>
    public string Absolute(HttpRequest? request, string logicalPathAndQuery)
    {
        ArgumentNullException.ThrowIfNull(logicalPathAndQuery);
        return Origin(request) + paths.Page(logicalPathAndQuery);
    }

    /// <summary>Origin plus a file URL (sitemap, image); never slashed.</summary>
    public string AbsoluteAsset(HttpRequest? request, string assetPath)
    {
        ArgumentNullException.ThrowIfNull(assetPath);
        return Origin(request) + paths.Asset(assetPath);
    }

    /// <summary>Scheme, host and port only; the path base is added by <see cref="SitePaths"/>.</summary>
    public string Origin(HttpRequest? request)
    {
        string? configured = options.Value.PublicBaseUrl;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            // The configured text is kept as written (host case, explicit default port), so a URL without a path
            // produces exactly what it did before the path base existed.
            int authorityStart = configured.IndexOf("://", StringComparison.Ordinal);
            int pathStart = authorityStart < 0 ? -1 : configured.AsSpan(authorityStart + 3).IndexOfAny('/', '?', '#');
            return pathStart < 0 ? configured.TrimEnd('/') : configured[..(authorityStart + 3 + pathStart)];
        }

        return request is null ? string.Empty : $"{request.Scheme}://{request.Host}";
    }
}
