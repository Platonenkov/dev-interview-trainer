using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Trainer.StaticSite;

/// <param name="Root">The exported site directory.</param>
/// <param name="SiteUrl">The public site URL; its path is the prefix the emulator serves under.</param>
/// <param name="Port">0 picks a free port.</param>
/// <param name="Gzip">Compress text, JavaScript, JSON, XML and WebAssembly like GitHub Pages does (never Brotli).</param>
public sealed record PagesEmulatorOptions(string Root, Uri SiteUrl, int Port = 0, bool Gzip = true);

/// <summary>
/// Serves an exported site the way GitHub Pages serves a project site: a file as it is, a directory URL with a trailing slash
/// from its <c>index.html</c>, a directory without one as a 301, a miss as 404 with the body of <c>404.html</c>, and no
/// response header but <c>Cache-Control: max-age=600</c>, the content type and the encoding. The test hooks inject what a real
/// host does on a bad day (a failure, a slow answer, a new deployment).
/// </summary>
public sealed class PagesEmulator : IAsyncDisposable
{
    private const string CacheControl = "max-age=600";

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".xml"] = "application/xml",
        [".txt"] = "text/plain; charset=utf-8",
        [".wasm"] = "application/wasm",
        [".png"] = "image/png",
        [".svg"] = "image/svg+xml",
        [".woff2"] = "font/woff2",
        [".webmanifest"] = "application/manifest+json",
    };

    private readonly WebApplication _app;
    private readonly string _pathBase;
    private readonly bool _gzip;
    private readonly ConcurrentDictionary<string, int> _failures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _extras = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TimeSpan> _delays = new(StringComparer.Ordinal);
    private volatile string _root;

    private PagesEmulator(WebApplication app, PagesEmulatorOptions options, Uri baseAddress)
    {
        _app = app;
        _root = Path.GetFullPath(options.Root);
        _pathBase = SiteAddress.PathBaseOf(options.SiteUrl);
        _gzip = options.Gzip;
        BaseAddress = baseAddress;
    }

    /// <summary>The server's address under the site prefix, with a trailing slash: <c>http://127.0.0.1:5000/dit/</c>.</summary>
    public Uri BaseAddress { get; }

    public static async Task<PagesEmulator> StartAsync(PagesEmulatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.SiteUrl);
        if (!Directory.Exists(options.Root))
        {
            throw new DirectoryNotFoundException($"The site directory '{options.Root}' does not exist.");
        }

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{options.Port}");
        // Pages sends no Server header either.
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);
        WebApplication app = builder.Build();

        // The handler needs the instance (swapped root, hooks), which needs the bound address: wire it through a holder.
        PagesEmulator[] holder = new PagesEmulator[1];
        app.Run(context => holder[0].HandleAsync(context));
        await app.StartAsync();

        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        string pathBase = SiteAddress.PathBaseOf(options.SiteUrl);
        PagesEmulator emulator = new(app, options, new Uri(address.TrimEnd('/') + pathBase + "/"));
        holder[0] = emulator;
        return emulator;
    }

    /// <summary>Serves another directory from now on: the next deployment.</summary>
    public void SwapRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    /// <summary>Answers the next request for <paramref name="sitePath"/> (a full path such as <c>/dit/js/x.js</c>) with <paramref name="status"/>, then serves normally.</summary>
    public void FailOnce(string sitePath, int status)
    {
        ArgumentException.ThrowIfNullOrEmpty(sitePath);
        _failures[sitePath] = status;
    }

    /// <summary>Holds every response whose path starts with <paramref name="sitePathPrefix"/> for <paramref name="delay"/>.</summary>
    public void Delay(string sitePathPrefix, TimeSpan delay)
    {
        ArgumentException.ThrowIfNullOrEmpty(sitePathPrefix);
        _delays[sitePathPrefix] = delay;
    }

    /// <summary>Serves <paramref name="file"/>, which lives outside the root, at a test-only path (the previous build's page).</summary>
    public void MapExtra(string sitePath, string file)
    {
        ArgumentException.ThrowIfNullOrEmpty(sitePath);
        ArgumentException.ThrowIfNullOrEmpty(file);
        _extras[sitePath] = Path.GetFullPath(file);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task HandleAsync(HttpContext context)
    {
        string path = context.Request.Path.Value ?? "/";
        foreach (KeyValuePair<string, TimeSpan> delay in _delays)
        {
            if (path.StartsWith(delay.Key, StringComparison.Ordinal))
            {
                await Task.Delay(delay.Value, context.RequestAborted);
            }
        }

        if (_failures.TryRemove(path, out int failure))
        {
            context.Response.StatusCode = failure;
            return;
        }

        string root = _root;
        if (_extras.TryGetValue(path, out string? extra) && File.Exists(extra))
        {
            await SendAsync(context, 200, extra);
            return;
        }

        StaticResolution resolution = StaticSiteLayout.Resolve(path, _pathBase, relative => File.Exists(FullPath(root, relative)));
        switch (resolution.Status)
        {
            case 200:
                await SendAsync(context, 200, FullPath(root, resolution.File!));
                break;
            case 301:
                context.Response.StatusCode = StatusCodes.Status301MovedPermanently;
                context.Response.Headers.Location = resolution.Location + context.Request.QueryString;
                break;
            default:
                string notFound = FullPath(root, StaticSiteLayout.NotFoundFile);
                if (File.Exists(notFound))
                {
                    await SendAsync(context, 404, notFound);
                }
                else
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                }

                break;
        }
    }

    private static string FullPath(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private async Task SendAsync(HttpContext context, int status, string file)
    {
        string contentType = ContentTypes.GetValueOrDefault(Path.GetExtension(file), "application/octet-stream");
        byte[] body = await File.ReadAllBytesAsync(file, context.RequestAborted);
        HttpResponse response = context.Response;
        response.StatusCode = status;
        response.ContentType = contentType;
        response.Headers.CacheControl = CacheControl;

        if (_gzip && IsCompressible(contentType) && AcceptsGzip(context.Request))
        {
            using MemoryStream compressed = new();
            await using (GZipStream gzip = new(compressed, new ZLibCompressionOptions { CompressionLevel = 6 }, leaveOpen: true))
            {
                await gzip.WriteAsync(body, context.RequestAborted);
            }

            body = compressed.ToArray();
            response.Headers.ContentEncoding = "gzip";
        }

        response.ContentLength = body.Length;
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await response.Body.WriteAsync(body, context.RequestAborted);
        }
    }

    private static bool IsCompressible(string contentType) =>
        contentType.StartsWith("text/", StringComparison.Ordinal)
        || contentType.StartsWith("application/json", StringComparison.Ordinal)
        || contentType.StartsWith("application/xml", StringComparison.Ordinal)
        || contentType.StartsWith("application/wasm", StringComparison.Ordinal)
        || contentType.StartsWith("image/svg+xml", StringComparison.Ordinal);

    private static bool AcceptsGzip(HttpRequest request) =>
        request.Headers.AcceptEncoding.Any(value => value is not null && value.Contains("gzip", StringComparison.OrdinalIgnoreCase));
}
