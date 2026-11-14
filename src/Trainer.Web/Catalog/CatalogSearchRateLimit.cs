using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Localization;
using Microsoft.Net.Http.Headers;

namespace Trainer.Web.Catalog;

/// <summary>
/// The per-address limit on catalog requests that carry a search term (ADR 0011). Those are the only anonymous public
/// requests that are never output-cached, so they are the only ones that always reach PostgreSQL; everything else
/// public is served from the cache and is not counted.
/// </summary>
public static class CatalogSearchRateLimit
{
    /// <summary>The partition of requests without a client address.</summary>
    public const string FallbackPartition = "search:unknown";

    private const string CatalogSegment = "questions";

    /// <summary>
    /// True for a GET or HEAD to <c>/{culture}/{stack}/questions</c> whose <c>q</c> parses to a non-empty term, exactly
    /// the requests <c>PublicPageCachePolicy</c> refuses to cache. A blank term is no search.
    /// </summary>
    public static bool IsSearchRequest(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        HttpRequest request = context.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            return false;
        }

        return IsCatalogPath(request.Path) && CatalogQueryParser.ParseSearch(request.Query) is not null;
    }

    /// <summary>The client address (after forwarded headers), whether or not the visitor is signed in.</summary>
    public static string Partition(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Connection.RemoteIpAddress is { } address ? "search:ip:" + address : FallbackPartition;
    }

    /// <summary>
    /// Writes the 429 page: a small localised HTML document (never JSON, this is a page and not <c>/api</c>) that is
    /// noindex and not cacheable and links back to the catalog without the term. The culture is already set by the
    /// request localisation middleware that runs before the limiter.
    /// </summary>
    public static async Task WriteRejectionAsync(HttpContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        HttpResponse response = context.Response;
        response.ContentType = "text/html; charset=utf-8";
        response.Headers[HeaderNames.CacheControl] = "no-store";
        response.Headers["X-Robots-Tag"] = "noindex";
        if (HttpMethods.IsHead(context.Request.Method))
        {
            return;
        }

        IStringLocalizer<SharedResource> localizer = context.RequestServices.GetRequiredService<IStringLocalizer<SharedResource>>();
        HtmlEncoder html = context.RequestServices.GetRequiredService<HtmlEncoder>();
        string title = localizer["Search.RateLimited.Title"];
        StringBuilder page = new();
        page.Append("<!DOCTYPE html>\n<html lang=\"")
            .Append(html.Encode(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName))
            .Append("\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .Append("<meta name=\"robots\" content=\"noindex\"><title>")
            .Append(html.Encode(title)).Append(" · ").Append(html.Encode(localizer["Site.Name"]))
            .Append("</title><link rel=\"stylesheet\" href=\"/css/tokens.css\"><link rel=\"stylesheet\" href=\"/css/site.css\"></head>")
            .Append("<body><main class=\"container main\"><h1>").Append(html.Encode(title)).Append("</h1><p>")
            .Append(html.Encode(localizer["Search.RateLimited.Body"])).Append("</p><p><a class=\"btn\" href=\"")
            .Append(html.Encode(context.Request.Path.Value ?? "/")).Append("\">")
            .Append(html.Encode(localizer["Search.RateLimited.Back"])).Append("</a></p></main></body></html>");
        await response.WriteAsync(page.ToString(), Encoding.UTF8, ct);
    }

    private static bool IsCatalogPath(PathString path)
    {
        string[] segments = (path.Value ?? string.Empty).Trim('/').Split('/');
        return segments.Length == 3 && string.Equals(segments[2], CatalogSegment, StringComparison.OrdinalIgnoreCase);
    }
}
