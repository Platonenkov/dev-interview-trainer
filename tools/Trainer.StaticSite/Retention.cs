using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;
using AngleSharp.Html.Parser;

namespace Trainer.StaticSite;

/// <param name="LiveSiteUrl">The public URL of the live site, the previous deployment.</param>
/// <param name="LiveBuildId">The build id the deployment gate read from the live <c>build.json</c>.</param>
/// <param name="PreviousPageOut">A file that receives the live Practice page of that build, or null.</param>
public sealed record RetentionOptions(Uri LiveSiteUrl, string LiveBuildId, string? PreviousPageOut);

/// <summary>
/// Keeps the previous deployment's immutable files alive for one more deployment (spec 5.13): a page of build A that is still
/// open or cached after B went live finds the runtime, content and card files it names. Uses its own HTTP client, because it
/// is the one step that talks to the live site.
/// </summary>
public sealed partial class Retention(HttpMessageHandler handler, TimeProvider time, Func<TimeSpan, CancellationToken, Task> delay)
{
    /// <summary>How long disagreeing copies are retried: a CDN can serve two files of one site from two deployments for a while.</summary>
    public static readonly TimeSpan PropagationWindow = TimeSpan.FromMinutes(3);

    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    /// <summary>The page whose copy of the previous build the smoke test runs against the new artifact.</summary>
    public const string PreviousPagePath = "en/dotnet/practice/";

    // A runtime file is a few megabytes; the cap keeps a hostile or broken stream from filling the build machine's memory.
    private const int MaxDecodedBytes = 256 * 1024 * 1024;

    private static readonly HtmlParser Parser = new();

    public async Task ApplyAsync(RetentionOptions options, string outDir, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.LiveBuildId);
        string root = StaticSiteLayout.NormalizeRoot(outDir);
        Uri live = Normalize(options.LiveSiteUrl);
        Regex eligible = ReadEligibility(root);

        using HttpClient client = new(new OriginGuardHandler(handler, live), disposeHandler: false) { Timeout = TimeSpan.FromMinutes(5) };
        HttpFetcher fetcher = new(client, delay);

        ImmutableInfo previous = await ReadPreviousManifestsAsync(fetcher, live, options.LiveBuildId, ct);
        Regex liveRule = ReadLiveEligibility(previous, eligible);
        string[] copied = await CopyFilesAsync(fetcher, live, previous, root, liveRule, ct);
        await File.WriteAllBytesAsync(
            Path.Combine(root, "retained.json"),
            ExportJsonContext.Serialize(new RetainedInfo(options.LiveBuildId, copied), ExportJsonContext.Default.RetainedInfo),
            ct);

        if (options.PreviousPageOut is { Length: > 0 } pageOut)
        {
            await SavePreviousPageAsync(fetcher, live, options.LiveBuildId, pageOut, ct);
        }
    }

    private static Uri Normalize(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) || url.Query.Length > 0 || url.Fragment.Length > 0)
        {
            throw new ExportFailedException($"The live site URL '{url}' must be an absolute http(s) URL without a query or fragment.");
        }

        return url.AbsolutePath.EndsWith('/') ? url : new Uri(url.GetLeftPart(UriPartial.Path) + "/");
    }

    private long Stamp() => time.GetUtcNow().ToUnixTimeMilliseconds();

    // Both manifests must come from the deployment the gate identified. The query defeats the CDN's cached copy; a copy that
    // is still stale is retried until the window closes, then the build fails rather than guess.
    private async Task<ImmutableInfo> ReadPreviousManifestsAsync(HttpFetcher fetcher, Uri live, string liveBuildId, CancellationToken ct)
    {
        DateTimeOffset deadline = time.GetUtcNow() + PropagationWindow;
        while (true)
        {
            string problem;
            try
            {
                (BuildInfo? build, ImmutableInfo? immutable, problem) = await TryReadManifestsAsync(fetcher, live, liveBuildId, ct);
                if (immutable is not null && build is not null)
                {
                    return immutable;
                }
            }
            catch (JsonException ex)
            {
                problem = $"a manifest is not valid JSON ({ex.Message})";
            }

            if (time.GetUtcNow() >= deadline)
            {
                throw new ExportFailedException(
                    $"The live manifests of {live} did not settle on build '{liveBuildId}' within {PropagationWindow.TotalMinutes:0} minutes: {problem}.");
            }

            await delay(PollInterval, ct);
        }
    }

    private async Task<(BuildInfo? Build, ImmutableInfo? Immutable, string Problem)> TryReadManifestsAsync(
        HttpFetcher fetcher, Uri live, string liveBuildId, CancellationToken ct)
    {
        FetchResult buildResult = await fetcher.GetAsync(new Uri(live, "build.json?_r=" + Stamp()), ct);
        FetchResult immutableResult = await fetcher.GetAsync(new Uri(live, "immutable.json?_r=" + Stamp()), ct);
        if (buildResult.Status != 200 || immutableResult.Status != 200)
        {
            return (null, null, $"build.json answered {buildResult.Status}, immutable.json answered {immutableResult.Status}");
        }

        BuildInfo? build = JsonSerializer.Deserialize(buildResult.Body, ExportJsonContext.Default.BuildInfo);
        ImmutableInfo? immutable = JsonSerializer.Deserialize(immutableResult.Body, ExportJsonContext.Default.ImmutableInfo);
        if (build is null || immutable is null)
        {
            return (null, null, "a manifest is empty");
        }

        if (!string.Equals(build.BuildId, liveBuildId, StringComparison.Ordinal) || !string.Equals(immutable.BuildId, liveBuildId, StringComparison.Ordinal))
        {
            return (null, null, $"build.json carries '{build.BuildId}' and immutable.json carries '{immutable.BuildId}', the gate read '{liveBuildId}'");
        }

        return (build, immutable, string.Empty);
    }

    [GeneratedRegex(@"^data/.+\.(?<hash>[0-9a-f]{16})\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex ShardPattern();

    private static Regex ReadEligibility(string root)
    {
        string path = Path.Combine(root, "immutable.json");
        ImmutableInfo? own = File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllBytes(path), ExportJsonContext.Default.ImmutableInfo) : null;
        if (string.IsNullOrEmpty(own?.EligiblePattern))
        {
            throw new ExportFailedException("The export has no immutable.json with an eligibility pattern: run retention after the crawl.");
        }

        return EligibilityRule.Compile(own.EligiblePattern);
    }

    // The live build's names are judged by the rule that build published with them: a commit that narrows or changes the rule
    // must not make every later deployment refuse the names of the build before it. A manifest without a rule (written before
    // the field existed) falls back to this build's rule.
    private static Regex ReadLiveEligibility(ImmutableInfo previous, Regex own)
    {
        if (string.IsNullOrEmpty(previous.EligiblePattern))
        {
            return own;
        }

        try
        {
            return EligibilityRule.Compile(previous.EligiblePattern);
        }
        catch (ArgumentException ex)
        {
            throw new ExportFailedException($"The live immutable.json has an eligibility pattern that is not a valid regex ({ex.Message}).", ex);
        }
    }

    // Only names the live build itself could have produced are taken from a live site: the worker's content-addressed names, Open
    // Graph cards and content shards. Anything else (a page, a manifest, a script without a fingerprint) would put a file of
    // the previous deployment next to the new one under a name that is allowed to change.
    private static void RequireRetainable(string name, Regex liveRule)
    {
        bool allowed = !name.EndsWith(".html", StringComparison.Ordinal)
            && (liveRule.IsMatch(name)
                || (name.StartsWith("og/", StringComparison.Ordinal) && name.EndsWith(".png", StringComparison.Ordinal))
                || (name.StartsWith("data/", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal)));
        if (!StaticSiteLayout.IsPlainRelativePath(name) || name.EndsWith('/') || !allowed)
        {
            throw new ExportFailedException($"The live immutable.json lists '{name}', which is not a content-addressed file, an Open Graph card or a content shard.");
        }
    }

    // A name the worker treats as immutable must mean the same bytes in both builds, so it is compared. An Open Graph card
    // is named after its text and a version, not its bytes, so the new build's copy simply wins. A missing file is copied.
    private static async Task<string[]> CopyFilesAsync(
        HttpFetcher fetcher, Uri live, ImmutableInfo previous, string root, Regex liveRule, CancellationToken ct)
    {
        ConcurrentBag<string> copied = [];
        ParallelOptions parallel = new() { MaxDegreeOfParallelism = SiteExporter.MaxInFlight, CancellationToken = ct };
        string[] names = [.. previous.Files.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        foreach (string name in names)
        {
            RequireRetainable(name, liveRule);
        }

        await Parallel.ForEachAsync(names, parallel, async (name, token) =>
        {
            string full = Path.GetFullPath(Path.Combine(root, name));
            if (!StaticSiteLayout.IsInside(root, full))
            {
                throw new ExportFailedException($"The live immutable.json lists '{name}', which would leave the output directory.");
            }

            bool present = File.Exists(full);
            bool compare = liveRule.IsMatch(name);
            if (present && !compare)
            {
                return;
            }

            Uri url = new(live, string.Join('/', name.Split('/').Select(Uri.EscapeDataString)));
            FetchResult result = await fetcher.GetAsync(url, token);
            if (result.Status != 200)
            {
                throw new ExportFailedException($"GET {url} answered {result.Status}, expected 200: the live immutable.json lists a file that is gone.");
            }

            RejectHtmlBody(url, name, result);
            if (present)
            {
                byte[] own = await File.ReadAllBytesAsync(full, token);
                if (!own.AsSpan().SequenceEqual(result.Body) && !SameDecodedBytes(name, full, own, result.Body))
                {
                    throw new ExportFailedException(
                        $"'{name}' has different bytes in the live site and in this export. A content-addressed name must always mean the same bytes.");
                }

                // Equal bytes, or equal once decoded: this build's own copy stays.
                return;
            }

            if (ShardPattern().Match(name) is { Success: true } shard
                && !string.Equals(shard.Groups["hash"].Value, Convert.ToHexStringLower(SHA256.HashData(result.Body))[..16], StringComparison.Ordinal))
            {
                throw new ExportFailedException($"The bytes of '{name}' on the live site do not match the hash in the name.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllBytesAsync(full, result.Body, token);
            copied.Add(name);
        });

        string[] ordered = [.. copied];
        Array.Sort(ordered, StringComparer.Ordinal);
        return ordered;
    }

    // A precompressed file carries the fingerprint of the file it decodes to (ADR 0027), so a compressor change leaves the name
    // alone while the bytes differ. The loader checks the decoded bytes, so two copies that decode to one file mean the same
    // thing and the new build's copy is kept. Any other kind of file has no such freedom. A decoder may end quietly on a
    // truncated stream, so two identically truncated copies would compare equal: the new build's decoded bytes must also be
    // its plain sibling, the file the loader verifies them against. The new copy is decoded and released before the live
    // one, so at most two decoded copies are held at once.
    private static bool SameDecodedBytes(string name, string full, byte[] own, byte[] live)
    {
        if (!name.EndsWith(".br", StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlyMemory<byte> ownDecoded = DecodeBrotli(name, own, "this export");
        string sibling = full[..^".br".Length];
        if (File.Exists(sibling) && !ownDecoded.Span.SequenceEqual(File.ReadAllBytes(sibling)))
        {
            return false;
        }

        return ownDecoded.Span.SequenceEqual(DecodeBrotli(name, live, "the live site").Span);
    }

    private static ReadOnlyMemory<byte> DecodeBrotli(string name, byte[] compressed, string where)
    {
        try
        {
            using MemoryStream input = new(compressed);
            using BrotliStream brotli = new(input, CompressionMode.Decompress);
            using MemoryStream output = new();
            byte[] buffer = new byte[81920];
            int read;
            while ((read = brotli.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > MaxDecodedBytes)
                {
                    throw new ExportFailedException($"'{name}' from {where} decodes to more than {MaxDecodedBytes} bytes.");
                }
            }

            // The stream owns the buffer and is disposed here, but the array stays valid and is not copied.
            return output.GetBuffer().AsMemory(0, (int)output.Length);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            throw new ExportFailedException($"'{name}' from {where} is not a valid Brotli stream ({ex.Message}).", ex);
        }
    }

    // A host that answers its 404 page with status 200 would otherwise turn a missing file into an HTML "file".
    private static void RejectHtmlBody(Uri url, string name, FetchResult result)
    {
        if (name.EndsWith(".html", StringComparison.Ordinal))
        {
            return;
        }

        ReadOnlySpan<byte> head = result.Body.AsSpan(0, Math.Min(result.Body.Length, 64));
        string text = System.Text.Encoding.UTF8.GetString(head).TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (string.Equals(result.MediaType, "text/html", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            throw new ExportFailedException($"GET {url} returned an HTML page for '{name}', not the file.");
        }
    }

    private async Task SavePreviousPageAsync(HttpFetcher fetcher, Uri live, string liveBuildId, string pageOut, CancellationToken ct)
    {
        DateTimeOffset deadline = time.GetUtcNow() + PropagationWindow;
        while (true)
        {
            Uri url = new(live, PreviousPagePath + "?_r=" + Stamp());
            FetchResult result = await fetcher.GetAsync(url, ct);
            string problem;
            if (result.Status != 200)
            {
                problem = $"it answered {result.Status}";
            }
            else
            {
                string? build = BuildOf(result.Body);
                if (string.Equals(build, liveBuildId, StringComparison.Ordinal))
                {
                    string full = Path.GetFullPath(pageOut);
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    await File.WriteAllBytesAsync(full, result.Body, ct);
                    return;
                }

                problem = $"its dit-build meta is '{build}'";
            }

            if (time.GetUtcNow() >= deadline)
            {
                throw new ExportFailedException(
                    $"The live page {PreviousPagePath} did not carry build '{liveBuildId}' within {PropagationWindow.TotalMinutes:0} minutes: {problem}.");
            }

            await delay(PollInterval, ct);
        }
    }

    private static string? BuildOf(byte[] html) =>
        Parser.ParseDocument(System.Text.Encoding.UTF8.GetString(html)).QuerySelector("meta[name='dit-build']")?.GetAttribute("content");
}
