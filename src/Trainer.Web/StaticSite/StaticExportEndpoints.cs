using Microsoft.Extensions.Options;
using Trainer.Application.StaticSite;
using Trainer.Contracts;
using Trainer.Domain.Keys;
using Trainer.Web.Analytics;
using Trainer.Web.Seo;
using Trainer.Web.Startup;

namespace Trainer.Web.StaticSite;

/// <summary>
/// What the export tool has to fetch, as the app itself lists it (spec 5.2). Every path is a site path under the
/// deployment's path base (<c>/dev-interview-trainer/en/</c>, <c>/dev-interview-trainer/css/site.css</c>), so the crawler never
/// assembles a URL from parts. The tool reads the same JSON with its own record.
/// </summary>
/// <param name="Pages">Pages answering 200: the root chooser, every sitemap page, retired question pages, island pages.</param>
/// <param name="Redirects">Pages answering a 301 (renamed question slugs).</param>
/// <param name="Files">Sitemaps, <c>robots.txt</c> and the service worker.</param>
/// <param name="Indexes">The content index of every culture and stack that has pages.</param>
/// <param name="Assets">Every static asset route of the application except precompressed variants (the Brotli runtime when enabled).</param>
/// <param name="Immutable">The subset of <paramref name="Assets"/> that is content-addressed.</param>
/// <param name="NotFoundPage">The page whose 404 answer becomes <c>404.html</c>.</param>
/// <param name="RedirectPageCsp">The policy the tool writes into redirect pages, which the app does not render.</param>
/// <param name="BuildId">The deployment identity the pages carry in <c>dit-build</c>; null for a local run.</param>
/// <param name="EligiblePattern">The service worker's eligibility rule as a JavaScript regex body (<see cref="StaticAssetRules.JavaScriptRegex"/>).</param>
/// <param name="GoogleSiteVerification">The configured <c>google-site-verification</c> value (spec 5.10), or null; the verifier checks the pages against it.</param>
/// <param name="YandexVerification">The configured <c>yandex-verification</c> value, or null.</param>
public sealed record StaticExportManifest(
    IReadOnlyList<string> Pages,
    IReadOnlyList<string> Redirects,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Indexes,
    IReadOnlyList<string> Assets,
    IReadOnlyList<string> Immutable,
    string NotFoundPage,
    string RedirectPageCsp,
    string? BuildId,
    string EligiblePattern,
    string? GoogleSiteVerification = null,
    string? YandexVerification = null)
{
    /// <summary>Segments that host an island, per culture and stack; they are not in the sitemap.</summary>
    private static readonly string[] IslandSegments = ["practice", "mock", "review", "profile"];

    /// <summary>The culture and stack of every stack home page in <paramref name="logicalPages"/> (<c>/ru/dotnet</c>).</summary>
    public static IReadOnlyList<(CultureKey Culture, StackKey Stack)> StackPages(IEnumerable<string> logicalPages)
    {
        ArgumentNullException.ThrowIfNull(logicalPages);
        List<(CultureKey, StackKey)> pairs = [];
        foreach (string page in logicalPages)
        {
            string[] segments = page.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 2
                && !string.Equals(segments[1], "about", StringComparison.Ordinal)
                && CultureKey.TryCreate(segments[0], out CultureKey culture)
                && StackKey.TryCreate(segments[1], out StackKey stack))
            {
                pairs.Add((culture, stack));
            }
        }

        return pairs;
    }

    /// <summary>Assembles the manifest from the app's own lists; <paramref name="indexPaths"/> are site-relative (<c>data/...</c>).</summary>
    public static StaticExportManifest Create(
        SitePaths paths,
        StaticPagePaths pagePaths,
        IEnumerable<string> indexPaths,
        StaticAssetList assets,
        string? buildId,
        string? goatCounterCode,
        StaticSiteOptions? siteOptions = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(pagePaths);
        ArgumentNullException.ThrowIfNull(indexPaths);
        ArgumentNullException.ThrowIfNull(assets);

        List<string> logicalPages = ["/", .. pagePaths.Pages];
        foreach ((CultureKey culture, StackKey stack) in StackPages(pagePaths.Pages))
        {
            foreach (string segment in IslandSegments)
            {
                logicalPages.Add($"/{culture.Value}/{stack.Value}/{segment}");
            }
        }

        List<string> files = ["sitemap.xml", "robots.txt", ServiceWorkerEndpoint.Route];
        files.AddRange(CultureKey.Supported.Select(c => $"sitemap-{c.Value}.xml"));

        return new StaticExportManifest(
            Ordered(logicalPages.Select(paths.Page)),
            Ordered(pagePaths.Redirects.Select(paths.Page)),
            Ordered(files.Select(paths.Asset)),
            Ordered(indexPaths.Select(paths.Asset)),
            Ordered(assets.ExportRoutes.Select(paths.Asset)),
            Ordered(assets.Immutable.Select(paths.Asset)),
            paths.Asset("not-found"),
            StaticCsp.For(islandPage: false, importMapHash: null, goatCounterCode),
            string.IsNullOrEmpty(buildId) ? null : buildId,
            StaticAssetRules.JavaScriptRegex,
            NullIfBlank(siteOptions?.GoogleSiteVerification),
            NullIfBlank(siteOptions?.YandexVerification));
    }

    // The page renders the tag only for a non-blank value, so a blank one means "not configured".
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string[] Ordered(IEnumerable<string> values)
    {
        string[] ordered = [.. values.Distinct(StringComparer.Ordinal)];
        Array.Sort(ordered, StringComparer.Ordinal);
        return ordered;
    }
}

/// <summary>
/// <c>GET /static-export/manifest.json</c>, mapped in the static edition only: the export tool's list of what to crawl.
/// Like the other static endpoints it sets no cookie and is never reachable on a server deployment, which cannot run the
/// static edition outside the export, Development and Testing environments.
/// </summary>
public static class StaticExportEndpoints
{
    public const string Route = "/static-export/manifest.json";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet(Route, BuildAsync);
    }

    private static async Task<IResult> BuildAsync(
        IStaticSiteQueries queries,
        StaticContentCatalog catalog,
        StaticAssetList assets,
        SitePaths paths,
        IOptions<AppOptions> app,
        IOptions<AnalyticsOptions> analytics,
        CancellationToken ct)
    {
        StaticPagePaths pagePaths = await queries.GetPagePathsAsync(ct);

        List<string> indexPaths = [];
        foreach ((CultureKey culture, StackKey stack) in StaticExportManifest.StackPages(pagePaths.Pages))
        {
            indexPaths.Add((await catalog.GetAsync(culture, stack, ct)).IndexPath);
        }

        StaticExportManifest manifest = StaticExportManifest.Create(
            paths, pagePaths, indexPaths, assets, app.Value.StaticSite.BuildId, analytics.Value.GoatCounterCode, app.Value.StaticSite);
        return Results.Json(manifest);
    }
}
