using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Trainer.Contracts;

namespace Trainer.StaticSite;

/// <param name="AppUrl">The transport address of the running app, under its path base (<c>http://127.0.0.1:5080/dit</c>).</param>
/// <param name="SiteUrl">The public site URL every saved file says (<c>https://owner.github.io/dit/</c>).</param>
/// <param name="OutDir">An empty or missing directory the site is written to.</param>
/// <param name="BuildId">The deployment identity; the app must run with the same <c>App:StaticSite:BuildId</c>.</param>
public sealed record ExportOptions(Uri AppUrl, Uri SiteUrl, string OutDir, string BuildId, string Commit);

/// <summary>
/// Crawls the running static-edition app and writes the site: the pages the app lists in its export manifest (spec 5.2), the
/// redirects as redirect pages, the not-found page as <c>404.html</c>, every asset, the content indexes and the question files
/// they name, and the Open Graph image each page points at. Then it writes <c>build.json</c> and <c>immutable.json</c>.
/// </summary>
public sealed partial class SiteExporter(HttpMessageHandler handler, TimeProvider time, Func<TimeSpan, CancellationToken, Task> delay)
{
    public const int MaxInFlight = 4;
    public const string ManifestRoute = "/static-export/manifest.json";

    private static readonly HtmlParser Parser = new();

    [GeneratedRegex(@"^data/.+\.(?<hash>[0-9a-f]{16})\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex ShardPattern();

    // ULIDs and the ids of the test fixtures: one path segment of letters, digits, underscore and hyphen.
    [GeneratedRegex("^[0-9A-Za-z_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PathSegment();

    /// <exception cref="ExportFailedException">Anything the crawl cannot turn into a correct file, naming the URL.</exception>
    public async Task ExportAsync(ExportOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BuildId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Commit);
        SiteAddress address = new(options.SiteUrl, options.AppUrl);
        string root = PrepareOutput(options.OutDir);

        using HttpClient client = new(new OriginGuardHandler(handler, address.AppUrl), disposeHandler: false)
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        HttpFetcher fetcher = new(client, delay);
        ExportManifest manifest = await ReadManifestAsync(fetcher, address, options.BuildId, ct);
        Crawl crawl = new(fetcher, address, manifest, root, options.BuildId);
        await crawl.RunAsync(ct);

        await File.WriteAllBytesAsync(
            Path.Combine(root, "build.json"),
            ExportJsonContext.Serialize(
                new BuildInfo(options.BuildId, options.Commit, time.GetUtcNow(), manifest.GoogleSiteVerification, manifest.YandexVerification),
                ExportJsonContext.Default.BuildInfo),
            ct);
        await File.WriteAllBytesAsync(
            Path.Combine(root, "immutable.json"),
            ExportJsonContext.Serialize(
                new ImmutableInfo(options.BuildId, crawl.ImmutableFiles(), manifest.EligiblePattern),
                ExportJsonContext.Default.ImmutableInfo),
            ct);
    }

    private static string PrepareOutput(string outDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outDir);
        string root = StaticSiteLayout.NormalizeRoot(outDir);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            // A leftover file would be published with the site.
            throw new ExportFailedException($"The output directory '{root}' is not empty.");
        }

        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<ExportManifest> ReadManifestAsync(HttpFetcher fetcher, SiteAddress address, string buildId, CancellationToken ct)
    {
        Uri url = new(address.AppUrl.GetLeftPart(UriPartial.Authority) + address.AppPathBase + ManifestRoute);
        FetchResult result = await fetcher.GetAsync(url, ct);
        if (result.Status != 200)
        {
            throw new ExportFailedException($"GET {url} answered {result.Status}, expected 200: is the app running the static edition?");
        }

        ExportManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(result.Body, ExportJsonContext.Default.ExportManifest)
                ?? throw new ExportFailedException($"GET {url} returned an empty manifest.");
        }
        catch (JsonException ex)
        {
            throw new ExportFailedException($"GET {url} did not return a valid export manifest: {ex.Message}", ex);
        }

        if (!string.Equals(manifest.BuildId, buildId, StringComparison.Ordinal))
        {
            // The pages render the id into dit-build; a different one would make every page disagree with build.json.
            throw new ExportFailedException(
                $"The app reports build id '{manifest.BuildId}' but the export was asked for '{buildId}': start the app with App__StaticSite__BuildId={buildId}.");
        }

        if (string.IsNullOrEmpty(manifest.EligiblePattern) || string.IsNullOrEmpty(manifest.RedirectPageCsp))
        {
            throw new ExportFailedException($"The export manifest at {url} lacks the eligibility pattern or the redirect page policy.");
        }

        return manifest;
    }

    private sealed class Crawl(HttpFetcher fetcher, SiteAddress address, ExportManifest manifest, string root, string buildId)
    {
        private readonly ConcurrentDictionary<string, string> _written = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _images = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _content = new(StringComparer.Ordinal);

        public async Task RunAsync(CancellationToken ct)
        {
            string[] listed = [.. manifest.Pages, .. manifest.Redirects, .. manifest.Files, .. manifest.Indexes, .. manifest.Assets, .. manifest.Immutable, manifest.NotFoundPage];
            foreach (string path in listed)
            {
                if (!address.IsSitePath(path) || path.Contains('?', StringComparison.Ordinal) || path.Contains('#', StringComparison.Ordinal) || !IsPlain(path))
                {
                    throw new ExportFailedException($"The export manifest lists '{path}', which is not a plain path under '{address.PathBase}/'.");
                }
            }

            foreach (string indexPath in manifest.Indexes)
            {
                _content[SiteRelative(indexPath)] = 0;
            }

            ParallelOptions parallel = new() { MaxDegreeOfParallelism = MaxInFlight, CancellationToken = ct };
            await Parallel.ForEachAsync(manifest.Pages, parallel, (path, token) => SavePageAsync(path, token));
            await Parallel.ForEachAsync(manifest.Redirects, parallel, (path, token) => SaveRedirectAsync(path, token));
            await Parallel.ForEachAsync([.. manifest.Files, .. manifest.Indexes, .. manifest.Assets], parallel, (path, token) => SaveFileAsync(path, token));
            await SaveNotFoundPageAsync(ct);
            await Parallel.ForEachAsync(_images.Keys.Order(StringComparer.Ordinal).ToArray(), parallel, (path, token) => SaveFileAsync(path, token));
            await Parallel.ForEachAsync(QuestionPaths().ToArray(), parallel, (path, token) => SaveFileAsync(path, token));
        }

        /// <summary>The content-addressed files: immutable assets, Open Graph images and content files, site-relative and sorted.</summary>
        public string[] ImmutableFiles()
        {
            HashSet<string> files = new(StringComparer.Ordinal);
            foreach (string path in manifest.Immutable)
            {
                files.Add(SiteRelative(path));
            }

            foreach (string path in _images.Keys)
            {
                files.Add(SiteRelative(path));
            }

            foreach (string file in _content.Keys)
            {
                files.Add(file);
            }

            string[] ordered = [.. files];
            Array.Sort(ordered, StringComparer.Ordinal);
            return ordered;
        }

        private bool IsPlain(string sitePath)
        {
            try
            {
                StaticSiteLayout.FileFor(sitePath, address.PathBase);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private string SiteRelative(string sitePath) => StaticSiteLayout.FileFor(sitePath, address.PathBase);

        private async ValueTask SavePageAsync(string sitePath, CancellationToken ct)
        {
            FetchResult result = await FetchAsync(sitePath, ct);
            Expect(result, 200);
            await SaveAsync(sitePath, result.Body, ct);
            CollectImage(result);
        }

        private async ValueTask SaveFileAsync(string sitePath, CancellationToken ct)
        {
            FetchResult result = await FetchAsync(sitePath, ct);
            Expect(result, 200);
            await SaveAsync(sitePath, result.Body, ct);
        }

        private async ValueTask SaveNotFoundPageAsync(CancellationToken ct)
        {
            FetchResult result = await FetchAsync(manifest.NotFoundPage, ct);
            Expect(result, 404);
            RequireRedirectPolicy(result);
            await WriteAsync(StaticSiteLayout.NotFoundFile, result.Body, ct);
            CollectImage(result);
        }

        // Redirect pages are written by the tool with the manifest's policy; the app renders the not-found page with its own.
        // They are the same public-page policy, so a difference means the two sides of the export disagree.
        private void RequireRedirectPolicy(FetchResult notFound)
        {
            AngleSharp.Html.Dom.IHtmlDocument document = Parser.ParseDocument(System.Text.Encoding.UTF8.GetString(notFound.Body));
            AngleSharp.Dom.IElement? first = document.Head is { Children.Length: > 0 } head ? head.Children[0] : null;
            string? policy = string.Equals(first?.GetAttribute("http-equiv"), "Content-Security-Policy", StringComparison.OrdinalIgnoreCase)
                ? first!.GetAttribute("content")
                : null;
            if (!string.Equals(policy, manifest.RedirectPageCsp, StringComparison.Ordinal))
            {
                throw new ExportFailedException(
                    $"The not-found page {notFound.Url} carries a Content-Security-Policy that differs from the manifest's RedirectPageCsp, which the redirect pages use.");
            }
        }

        private async ValueTask SaveRedirectAsync(string sitePath, CancellationToken ct)
        {
            FetchResult result = await FetchAsync(sitePath, ct);
            if (result.Status is not (301 or 308))
            {
                throw new ExportFailedException($"GET {result.Url} answered {result.Status}, expected a 301 or 308 redirect.");
            }

            if (result.Location is not { } location)
            {
                throw new ExportFailedException($"GET {result.Url} answered a redirect without a Location header.");
            }

            if (!address.TryReadSiteUrl(location, out string targetPath, out string query))
            {
                throw new ExportFailedException(
                    $"GET {result.Url} redirects to {location}, which is outside the site {address.SiteUrl} and the app {address.AppUrl}.");
            }

            string html = RedirectPage.Html(address.PublicUrl(targetPath + query).AbsoluteUri, manifest.RedirectPageCsp, buildId);
            await SaveAsync(sitePath, System.Text.Encoding.UTF8.GetBytes(html), ct);
        }

        private Task<FetchResult> FetchAsync(string sitePathAndQuery, CancellationToken ct) =>
            fetcher.GetAsync(address.ToTransport(sitePathAndQuery), ct);

        private static void Expect(FetchResult result, int status)
        {
            if (result.Status != status)
            {
                throw new ExportFailedException($"GET {result.Url} answered {result.Status}, expected {status}.");
            }
        }

        private ValueTask SaveAsync(string sitePath, byte[] body, CancellationToken ct) => WriteAsync(SiteRelative(sitePath), body, ct);

        private async ValueTask WriteAsync(string relative, byte[] body, CancellationToken ct)
        {
            string hash = Convert.ToHexString(SHA256.HashData(body));
            if (!_written.TryAdd(relative, hash))
            {
                if (!string.Equals(_written[relative], hash, StringComparison.Ordinal))
                {
                    throw new ExportFailedException($"'{relative}' was saved twice with different bytes.");
                }

                return;
            }

            if (ShardPattern().Match(relative) is { Success: true } shard
                && !string.Equals(shard.Groups["hash"].Value, Convert.ToHexStringLower(SHA256.HashData(body))[..16], StringComparison.Ordinal))
            {
                // The name promises the bytes; a compressed or altered copy would break every client that checks it.
                throw new ExportFailedException($"The bytes saved as '{relative}' do not match the hash in the name.");
            }

            string full = Path.GetFullPath(Path.Combine(root, relative));
            if (!StaticSiteLayout.IsInside(root, full))
            {
                throw new ExportFailedException($"'{relative}' would be written outside the output directory.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllBytesAsync(full, body, ct);
        }

        // The saved page names its card with an absolute public URL; the card is fetched through the transport address.
        private void CollectImage(FetchResult page)
        {
            AngleSharp.Html.Dom.IHtmlDocument document = Parser.ParseDocument(System.Text.Encoding.UTF8.GetString(page.Body));
            foreach (AngleSharp.Dom.IElement meta in document.QuerySelectorAll("meta[property='og:image']"))
            {
                string? content = meta.GetAttribute("content");
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                if (!Uri.TryCreate(address.PublicUrl(PublicPathOf(page.Url)), content.Trim(), out Uri? image)
                    || !address.TryReadSiteUrl(image, out string path, out _))
                {
                    throw new ExportFailedException($"{page.Url} names the image '{content}', which is outside the site {address.SiteUrl}.");
                }

                _images.TryAdd(path, 0);
            }
        }

        private string PublicPathOf(Uri transportUrl)
        {
            address.TryReadSiteUrl(transportUrl, out string path, out string query);
            return path + query;
        }

        // Each question file named by a saved index. Reading the index here, after it was saved, keeps the hash check of
        // SaveAsync authoritative: a file is fetched only under the name the index gave it.
        private IEnumerable<string> QuestionPaths()
        {
            foreach (string indexPath in manifest.Indexes)
            {
                string relative = SiteRelative(indexPath);
                byte[] bytes = File.ReadAllBytes(Path.Combine(root, relative));
                StaticIndexDto index;
                try
                {
                    index = JsonSerializer.Deserialize(bytes, TrainerJsonContext.Default.StaticIndexDto)
                        ?? throw new ExportFailedException($"The content index '{relative}' is empty.");
                }
                catch (JsonException ex)
                {
                    throw new ExportFailedException($"The content index '{relative}' is not valid JSON: {ex.Message}", ex);
                }

                foreach (StaticIndexEntryDto question in index.Questions)
                {
                    // The id and the culture become path segments; a value that is not one would otherwise escape as an
                    // ArgumentException from the layout code instead of a failure that names the index.
                    string id = question.Id ?? string.Empty;
                    if (!SiteExporter.PathSegment().IsMatch(id))
                    {
                        throw new ExportFailedException($"The content index '{relative}' lists the malformed question id '{id}'.");
                    }

                    string file = StaticContentPaths.Question(index.Culture, id, question.Hash);
                    if (!IsPlain(address.PathBase + "/" + file))
                    {
                        throw new ExportFailedException($"The content index '{relative}' lists the question id '{id}', which does not make a plain file path.");
                    }

                    _content[file] = 0;
                    yield return address.PathBase + "/" + file;
                }
            }
        }
    }
}
