using System.Text.Json;
using Microsoft.Extensions.Options;
using Trainer.Web.Startup;

namespace Trainer.Web.StaticSite;

/// <summary>
/// The routes of the application's static web assets, read from <c>&lt;ApplicationName&gt;.staticwebassets.endpoints.json</c>
/// next to the application. The export (Task 16) copies <see cref="ExportRoutes"/>; <see cref="Immutable"/> is the subset
/// that carries a fingerprint (a precompressed variant when its base route does), the set the verifier compares with
/// <see cref="StaticAssetRules"/>.
/// </summary>
public sealed class StaticAssetList
{
    private readonly Lazy<Routes> _routes;

    public StaticAssetList(IWebHostEnvironment environment, IOptions<AppOptions> options)
        : this(ManifestPath(environment), options.Value.StaticSite.BrotliRuntime)
    {
    }

    private StaticAssetList(string manifestPath, bool keepBrotliRuntime) =>
        _routes = new Lazy<Routes>(() => Read(manifestPath, keepBrotliRuntime), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// A list over an explicit manifest. <paramref name="keepBrotliRuntime"/> keeps the <c>_framework/</c> <c>.br</c>
    /// routes, which <see cref="StaticSiteOptions.BrotliRuntime"/> turns on for the dependency-injected constructor.
    /// </summary>
    public static StaticAssetList FromManifest(string manifestPath, bool keepBrotliRuntime)
    {
        ArgumentException.ThrowIfNullOrEmpty(manifestPath);
        return new StaticAssetList(manifestPath, keepBrotliRuntime);
    }

    /// <summary>Every route except precompressed variants and selector variants, in manifest order, without duplicates.</summary>
    public IReadOnlyList<string> ExportRoutes => _routes.Value.Export;

    /// <summary>The fingerprinted routes of <see cref="ExportRoutes"/>; the unfingerprinted alias of a file is not in it.</summary>
    public IReadOnlyList<string> Immutable => _routes.Value.Immutable;

    /// <summary>
    /// The integrity table of the runtime files (<see cref="BootIntegrityModule"/>), or null when the Brotli runtime is off.
    /// Its route is in <see cref="ExportRoutes"/> and <see cref="Immutable"/>.
    /// </summary>
    public BootIntegrityModule? BootIntegrity => _routes.Value.BootIntegrity;

    private static string ManifestPath(IWebHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return Path.Combine(AppContext.BaseDirectory, environment.ApplicationName + ".staticwebassets.endpoints.json");
    }

    private const string FrameworkPrefix = "_framework/";

    // The files the loader decodes (types dotnetwasm, assembly, pdb and globalization). Scripts are covered by the import map.
    private static readonly string[] RuntimeExtensions = [".wasm", ".pdb", ".dat"];

    private static Routes Read(string manifestPath, bool keepBrotliRuntime)
    {
        using FileStream stream = File.OpenRead(manifestPath);
        using JsonDocument document = JsonDocument.Parse(stream);

        JsonElement[] endpoints = [.. document.RootElement.GetProperty("Endpoints").EnumerateArray()];

        // A precompressed variant carries only an integrity property in a real manifest, so it is judged by its base route.
        HashSet<string> fingerprinted = new(StringComparer.Ordinal);
        foreach (JsonElement endpoint in endpoints)
        {
            string route = endpoint.GetProperty("Route").GetString()!;
            if (IsFingerprinted(endpoint, route))
            {
                fingerprinted.Add(route);
            }
        }

        List<string> export = [];
        List<string> immutable = [];
        Dictionary<string, string> bootIntegrity = new(StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonElement endpoint in endpoints)
        {
            string route = endpoint.GetProperty("Route").GetString()!;
            bool selectorVariant = endpoint.GetProperty("Selectors").GetArrayLength() > 0;
            if (selectorVariant || !IsExported(route, keepBrotliRuntime) || !seen.Add(route))
            {
                continue;
            }

            export.Add(route);
            if (fingerprinted.Contains(route) || fingerprinted.Contains(WithoutCompressionSuffix(route)))
            {
                immutable.Add(route);
            }

            if (keepBrotliRuntime && fingerprinted.Contains(route) && IsRuntimeFile(route) && IntegrityOf(endpoint) is { } integrity)
            {
                bootIntegrity[route[FrameworkPrefix.Length..]] = integrity;
            }
        }

        // Only with the Brotli runtime: without it nothing decodes in the page and nothing needs verifying.
        BootIntegrityModule? table = keepBrotliRuntime ? BootIntegrityModule.Create(bootIntegrity) : null;
        if (table is not null)
        {
            export.Add(table.Route);
            immutable.Add(table.Route);
        }

        return new Routes(export, immutable, table);
    }

    // The base route's integrity is that of the file itself; a variant's is of its compressed bytes, which is why only
    // routes that are not variants (no suffix) are read.
    private static bool IsRuntimeFile(string route) =>
        route.StartsWith(FrameworkPrefix, StringComparison.Ordinal)
        && RuntimeExtensions.Any(extension => route.EndsWith(extension, StringComparison.Ordinal));

    private static string? IntegrityOf(JsonElement endpoint)
    {
        foreach (JsonElement property in endpoint.GetProperty("EndpointProperties").EnumerateArray())
        {
            if (string.Equals(property.GetProperty("Name").GetString(), "integrity", StringComparison.Ordinal))
            {
                return property.GetProperty("Value").GetString();
            }
        }

        return null;
    }

    private static string WithoutCompressionSuffix(string route) =>
        route.EndsWith(".br", StringComparison.Ordinal) || route.EndsWith(".gz", StringComparison.Ordinal) ? route[..^3] : route;

    private static bool IsExported(string route, bool keepBrotliRuntime)
    {
        if (route.EndsWith(".gz", StringComparison.Ordinal))
        {
            return false;
        }

        return !route.EndsWith(".br", StringComparison.Ordinal)
            || (keepBrotliRuntime && route.StartsWith("_framework/", StringComparison.Ordinal));
    }

    // The label is the unfingerprinted name; a route equal to it is an alias (or has no fingerprint) and is mutable.
    private static bool IsFingerprinted(JsonElement endpoint, string route)
    {
        string? fingerprint = null;
        string? label = null;
        foreach (JsonElement property in endpoint.GetProperty("EndpointProperties").EnumerateArray())
        {
            string name = property.GetProperty("Name").GetString()!;
            if (string.Equals(name, "fingerprint", StringComparison.Ordinal))
            {
                fingerprint = property.GetProperty("Value").GetString();
            }
            else if (string.Equals(name, "label", StringComparison.Ordinal))
            {
                label = property.GetProperty("Value").GetString();
            }
        }

        return !string.IsNullOrEmpty(fingerprint) && label is not null && !string.Equals(route, label, StringComparison.Ordinal);
    }

    private sealed record Routes(IReadOnlyList<string> Export, IReadOnlyList<string> Immutable, BootIntegrityModule? BootIntegrity);
}
