using System.Globalization;
using System.Text;
using System.Xml;
using Microsoft.Extensions.Options;
using Trainer.Application.Seo;
using Trainer.Web.Caching;
using Trainer.Domain.Keys;
using Trainer.Web.Routing;
using Trainer.Web.Seo;
using Trainer.Web.Startup;

namespace Trainer.Web.Endpoints;

/// <summary><c>/sitemap.xml</c> (index), one sitemap per culture and <c>/robots.txt</c>, all with absolute URLs.</summary>
public static class SitemapEndpoints
{
    public const string XmlContentType = "application/xml; charset=utf-8";
    public const string TextContentType = "text/plain; charset=utf-8";

    private const string SitemapNamespace = "http://www.sitemaps.org/schemas/sitemap/0.9";
    private const string XhtmlNamespace = "http://www.w3.org/1999/xhtml";

    // Island pages: not indexable, and crawlers have nothing useful to do there.
    private static readonly string[] InteractiveSegments = ["practice", "mock", "review", "profile"];

    private static readonly XmlWriterSettings WriterSettings = new()
    {
        Async = true,
        Indent = true,
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        CloseOutput = false,
    };

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/sitemap.xml", WriteIndexAsync).CacheOutput(PublicPageCachePolicy.PolicyName);
        endpoints.MapGet("/sitemap-{lang}.xml", WriteCultureSitemapAsync).CacheOutput(PublicPageCachePolicy.PolicyName);
        endpoints.MapGet("/robots.txt", WriteRobotsAsync).CacheOutput(PublicPageCachePolicy.PolicyName);
    }

    private static async Task WriteIndexAsync(HttpContext context, SiteUrls urls)
    {
        context.Response.ContentType = XmlContentType;
        await using XmlWriter writer = XmlWriter.Create(context.Response.Body, WriterSettings);
        await writer.WriteStartDocumentAsync();
        await writer.WriteStartElementAsync(null, "sitemapindex", SitemapNamespace);
        foreach (CultureKey culture in CultureKey.Supported)
        {
            await writer.WriteStartElementAsync(null, "sitemap", null);
            await writer.WriteElementStringAsync(null, "loc", null, urls.Absolute(context.Request, $"/sitemap-{culture.Value}.xml"));
            await writer.WriteEndElementAsync();
        }

        await writer.WriteEndElementAsync();
        await writer.WriteEndDocumentAsync();
    }

    private static async Task WriteCultureSitemapAsync(
        string lang, HttpContext context, SiteUrls urls, ISitemapQueries queries, IOptions<AppOptions> options)
    {
        if (!CultureKey.TryCreate(lang, out CultureKey key))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        IReadOnlyList<SitemapEntry> entries = await queries.GetAsync(key, context.RequestAborted);

        // The default stack home renders the same page as the bare culture home, whose URL is its canonical.
        string duplicateOfHome = $"/{key.Value}/{options.Value.DefaultStack}";

        context.Response.ContentType = XmlContentType;
        await using XmlWriter writer = XmlWriter.Create(context.Response.Body, WriterSettings);
        await writer.WriteStartDocumentAsync();
        await writer.WriteStartElementAsync(null, "urlset", SitemapNamespace);
        await writer.WriteAttributeStringAsync("xmlns", "xhtml", null, XhtmlNamespace);
        foreach (SitemapEntry entry in entries.Where(e => !string.Equals(e.Path, duplicateOfHome, StringComparison.Ordinal)))
        {
            await WriteEntryAsync(writer, entry, context.Request, urls);
        }

        await writer.WriteEndElementAsync();
        await writer.WriteEndDocumentAsync();
    }

    private static async Task WriteEntryAsync(XmlWriter writer, SitemapEntry entry, HttpRequest request, SiteUrls urls)
    {
        await writer.WriteStartElementAsync(null, "url", null);
        await writer.WriteElementStringAsync(null, "loc", null, urls.Absolute(request, entry.Path));
        if (entry.LastModified is { } modified)
        {
            await writer.WriteElementStringAsync(null, "lastmod", null, modified.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        foreach (CultureKey alternate in entry.Alternates)
        {
            await WriteAlternateAsync(writer, alternate.Value, urls.Absolute(request, CulturePath.SwitchCulture(entry.Path, alternate)));
        }

        CultureKey? defaultCulture = entry.Alternates.Count == 0
            ? null
            : entry.Alternates.Contains(CultureKey.Default) ? CultureKey.Default : entry.Alternates[0];
        if (defaultCulture is { } fallback)
        {
            await WriteAlternateAsync(writer, "x-default", urls.Absolute(request, CulturePath.SwitchCulture(entry.Path, fallback)));
        }

        await writer.WriteEndElementAsync();
    }

    private static async Task WriteAlternateAsync(XmlWriter writer, string hreflang, string href)
    {
        await writer.WriteStartElementAsync("xhtml", "link", XhtmlNamespace);
        await writer.WriteAttributeStringAsync(null, "rel", null, "alternate");
        await writer.WriteAttributeStringAsync(null, "hreflang", null, hreflang);
        await writer.WriteAttributeStringAsync(null, "href", null, href);
        await writer.WriteEndElementAsync();
    }

    private static async Task WriteRobotsAsync(HttpContext context, SiteUrls urls)
    {
        context.Response.ContentType = TextContentType;
        await using StreamWriter writer = new(context.Response.Body, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteLineAsync("User-agent: *");
        await writer.WriteLineAsync("Disallow: /api/");
        await writer.WriteLineAsync("Disallow: /signin-telegram/");
        await writer.WriteLineAsync("Disallow: /lang/");
        foreach (CultureKey culture in CultureKey.Supported)
        {
            await writer.WriteLineAsync($"Disallow: /{culture.Value}/signin");
            foreach (string segment in InteractiveSegments)
            {
                await writer.WriteLineAsync($"Disallow: /{culture.Value}/*/{segment}");
            }
        }

        await writer.WriteLineAsync();
        await writer.WriteLineAsync("Sitemap: " + urls.Absolute(context.Request, "/sitemap.xml"));
    }
}
