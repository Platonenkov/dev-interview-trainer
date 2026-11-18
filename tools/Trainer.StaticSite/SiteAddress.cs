namespace Trainer.StaticSite;

/// <summary>
/// The two origins of an export (spec 5.2). The public site URL is what every saved file says; the transport URL is where the
/// crawler fetches. Site paths are decoded and always start with the public path base.
/// </summary>
public sealed class SiteAddress
{
    public SiteAddress(Uri siteUrl, Uri appUrl)
    {
        SiteUrl = Validate(siteUrl, nameof(siteUrl));
        AppUrl = Validate(appUrl, nameof(appUrl));
        PathBase = PathBaseOf(SiteUrl);
        AppPathBase = PathBaseOf(AppUrl);
    }

    public Uri SiteUrl { get; }

    public Uri AppUrl { get; }

    /// <summary>Empty for a root site, otherwise <c>/dit</c>: leading slash, no trailing one.</summary>
    public string PathBase { get; }

    public string AppPathBase { get; }

    public static string PathBaseOf(Uri url) => Uri.UnescapeDataString(url.AbsolutePath).TrimEnd('/');

    public static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    public static bool IsUnder(string path, string prefix) =>
        prefix.Length == 0 || string.Equals(path, prefix, StringComparison.Ordinal) || path.StartsWith(prefix + "/", StringComparison.Ordinal);

    /// <summary>True when <paramref name="sitePath"/> (no query) is absolute and under the public path base.</summary>
    public bool IsSitePath(string sitePath) => sitePath.StartsWith('/') && IsUnder(sitePath, PathBase);

    /// <summary>The public absolute URL of a site path (with an optional query).</summary>
    public Uri PublicUrl(string sitePathAndQuery) => new(SiteUrl.GetLeftPart(UriPartial.Authority) + sitePathAndQuery, UriKind.Absolute);

    /// <summary>The transport URL that serves a site path (with an optional query): the public prefix mapped onto the transport prefix.</summary>
    /// <exception cref="ExportFailedException">The path is not under the public path base.</exception>
    public Uri ToTransport(string sitePathAndQuery)
    {
        int tail = sitePathAndQuery.AsSpan().IndexOf('?');
        string pathOnly = tail < 0 ? sitePathAndQuery : sitePathAndQuery[..tail];
        if (!IsSitePath(pathOnly))
        {
            throw new ExportFailedException($"'{sitePathAndQuery}' is not a path under the site prefix '{PathBase}/'.");
        }

        return new Uri(AppUrl.GetLeftPart(UriPartial.Authority) + AppPathBase + sitePathAndQuery[PathBase.Length..], UriKind.Absolute);
    }

    /// <summary>
    /// Maps an absolute URL met while crawling to a decoded site path and its query, when it is under the public site URL or
    /// the transport URL; false for anything else.
    /// </summary>
    public bool TryReadSiteUrl(Uri url, out string sitePath, out string query)
    {
        sitePath = string.Empty;
        query = url.Query;
        string path = Uri.UnescapeDataString(url.AbsolutePath);
        if (SameOrigin(url, SiteUrl) && IsUnder(path, PathBase))
        {
            sitePath = path;
            return true;
        }

        if (SameOrigin(url, AppUrl) && IsUnder(path, AppPathBase))
        {
            sitePath = PathBase + path[AppPathBase.Length..];
            return true;
        }

        return false;
    }

    private static Uri Validate(Uri url, string name)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri
            || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
            || url.Query.Length > 0
            || url.Fragment.Length > 0
            || url.UserInfo.Length > 0)
        {
            throw new ArgumentException("Must be an absolute http(s) URL without credentials, a query or a fragment.", name);
        }

        return url;
    }
}
