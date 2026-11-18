using Microsoft.Extensions.Options;
using Trainer.Web.Startup;

namespace Trainer.Web.Seo;

/// <summary>
/// The one place that turns logical site paths into URLs under the deployment's path base. GitHub Pages serves a project
/// site under <c>/{repository}/</c>, and only directory URLs (with a trailing slash) resolve without a redirect, so the
/// static edition slashes page URLs but never asset URLs.
/// </summary>
public sealed class SitePaths
{
    public SitePaths(IOptions<AppOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        AppOptions value = options.Value;
        PathBase = ReadPathBase(value.PublicBaseUrl);
        SlashedPages = value.Edition == SiteEdition.Static;
    }

    /// <summary>Empty, or a path with a leading slash and no trailing one, such as <c>/dev-interview-trainer</c>.</summary>
    public string PathBase { get; }

    public bool SlashedPages { get; }

    /// <summary>The value for the base element: the path base with a trailing slash.</summary>
    public string BaseHref => PathBase + "/";

    /// <summary>A page URL for a logical path such as <c>/ru/dotnet/questions?level=junior</c>; a fragment-only reference is kept.</summary>
    public string Page(string logicalPathAndQuery)
    {
        ArgumentNullException.ThrowIfNull(logicalPathAndQuery);
        if (logicalPathAndQuery.StartsWith('#') || logicalPathAndQuery.StartsWith('?'))
        {
            return logicalPathAndQuery;
        }

        int tail = logicalPathAndQuery.AsSpan().IndexOfAny('?', '#');
        string path = tail < 0 ? logicalPathAndQuery : logicalPathAndQuery[..tail];
        string rest = tail < 0 ? string.Empty : logicalPathAndQuery[tail..];

        if (path.Length == 0 || path == "/")
        {
            return BaseHref + rest;
        }

        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        if (SlashedPages && !path.EndsWith('/'))
        {
            path += "/";
        }

        return PathBase + path + rest;
    }

    /// <summary>A file URL (stylesheet, script, sitemap, image); never gets a trailing slash.</summary>
    public string Asset(string assetPath)
    {
        ArgumentNullException.ThrowIfNull(assetPath);
        return PathBase + "/" + assetPath.TrimStart('/');
    }

    /// <summary>The current request as a path-base-aware URL, for fragment links on island pages.</summary>
    public string CurrentPage(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return PathBase + request.Path + request.QueryString;
    }

    private static string ReadPathBase(string? publicBaseUrl)
    {
        if (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out Uri? uri))
        {
            return string.Empty;
        }

        return uri.AbsolutePath.TrimEnd('/');
    }
}
