using Microsoft.Extensions.Options;
using Trainer.Web.Startup;

namespace Trainer.Web.Seo;

/// <summary>
/// Builds absolute public URLs. <c>App:PublicBaseUrl</c> wins so canonical links and sitemaps stay stable behind a
/// reverse proxy; the request origin is only a fallback for Development and Testing, where startup validation
/// allows the setting to be empty.
/// </summary>
public sealed class SiteUrls(IOptions<AppOptions> options)
{
    public string Absolute(HttpRequest? request, string pathAndQuery)
    {
        ArgumentNullException.ThrowIfNull(pathAndQuery);
        return Origin(request) + pathAndQuery;
    }

    public string Origin(HttpRequest? request)
    {
        string? configured = options.Value.PublicBaseUrl;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }

        return request is null ? string.Empty : $"{request.Scheme}://{request.Host}";
    }
}
