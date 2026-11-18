using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Trainer.Contracts;

namespace Trainer.StaticSite.Tests;

/// <summary>Pages that satisfy every rule of the verifier, to be broken one rule at a time.</summary>
internal static class TestPages
{
    public const string BuildId = "abc123.7.1";

    /// <summary>The body of <c>StaticAssetRules.JavaScriptRegex</c> as the app serves it in the manifest.</summary>
    public const string EligiblePattern = @"^(?:.+\.[a-z0-9]{10}\.[A-Za-z0-9.]+|data\/.+\.[0-9a-f]{16}\.json)$";

    public const string RecoveryScript = "(function(){window.ditRecover=function(){};})();";

    public static readonly Uri Site = new("https://example.test/dit/");

    public static string RecoveryHash { get; } = Sha256Source(RecoveryScript);

    public static string Csp { get; } = $"default-src 'self'; script-src 'self' {RecoveryHash} https://gc.zgo.at; style-src 'self'; base-uri 'none'";

    public static string Sha256Source(string text) => "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(text))) + "'";

    public static string Html(
        string body,
        string head = "",
        string? buildId = BuildId,
        bool recovery = true,
        bool cspFirst = true,
        string? csp = null)
    {
        string policy = $"<meta http-equiv=\"Content-Security-Policy\" content=\"{csp ?? Csp}\">";
        string build = buildId is null ? string.Empty : $"<meta name=\"dit-build\" content=\"{buildId}\">";
        string script = recovery ? $"<script>{RecoveryScript}</script>" : string.Empty;
        return cspFirst
            ? $"<!DOCTYPE html><html><head>{policy}{build}{script}{head}<title>t</title></head><body>{body}</body></html>"
            : $"<!DOCTYPE html><html><head><title>t</title>{policy}{build}{script}{head}</head><body>{body}</body></html>";
    }

    public static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];
}

/// <summary>A temporary directory holding an exported site, valid until a test breaks it.</summary>
internal sealed class SiteDir : IDisposable
{
    public SiteDir()
    {
        Root = Path.Combine(Path.GetTempPath(), "dit-site-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public void Write(string relative, string text) => WriteBytes(relative, Encoding.UTF8.GetBytes(text));

    public void WriteBytes(string relative, byte[] bytes)
    {
        string full = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    public void WriteManifests(string[] immutable, string[]? retained = null, string buildId = TestPages.BuildId, string? google = null, string? yandex = null)
    {
        Write("build.json", JsonSerializer.Serialize(new BuildInfo(buildId, "deadbeef", DateTimeOffset.UnixEpoch, google, yandex), ExportJsonContext.Default.BuildInfo));
        Write("immutable.json", JsonSerializer.Serialize(new ImmutableInfo(buildId, immutable, TestPages.EligiblePattern), ExportJsonContext.Default.ImmutableInfo));
        if (retained is not null)
        {
            Write("retained.json", JsonSerializer.Serialize(new RetainedInfo("previous.1.1", retained), ExportJsonContext.Default.RetainedInfo));
        }
    }

    /// <summary>A small site that verifies clean: pages, a redirect page, 404.html, assets, sitemaps and manifests.</summary>
    public static SiteDir Valid()
    {
        SiteDir site = new();
        site.Write(
            "index.html",
            TestPages.Html(
                "<a href=\"/dit/en/\">English</a><a href=\"en/\">again</a><a href=\"?x=1\">q</a><img src=\"/dit/img/logo.0123456789.png\" srcset=\"/dit/img/logo.0123456789.png 1x, /dit/img/logo.0123456789.png 2x\">",
                "<link rel=\"stylesheet\" href=\"/dit/css/site.abcdefghij.css\"><link rel=\"canonical\" href=\"https://example.test/dit/\">"
                + "<script src=\"https://gc.zgo.at/count.js\"></script><script src=\"/dit/js/app.abcdefghij.js\"></script>"));
        site.Write("en/index.html", TestPages.Html("<a href=\"/dit/\">Home</a><a href=\"https://example.org/doc\" rel=\"nofollow noopener noreferrer\">Doc</a>"));
        site.Write("404.html", TestPages.Html("<a href=\"/dit/en/\">English</a>"));
        site.Write("old/index.html", RedirectPage.Html("https://example.test/dit/en/", TestPages.Csp, TestPages.BuildId));
        site.Write("css/site.abcdefghij.css", "@font-face{src:url(\"../fonts/x.abcdefghij.woff2\")}.a{background:url('../img/logo.0123456789.png')}");
        site.Write("fonts/x.abcdefghij.woff2", "font");
        site.Write("img/logo.0123456789.png", "png");
        site.Write("js/app.abcdefghij.js", "console.log(1);");
        site.Write(
            "sitemap.xml",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\" xmlns:xhtml=\"http://www.w3.org/1999/xhtml\">"
            + "<url><loc>https://example.test/dit/en/</loc><xhtml:link rel=\"alternate\" hreflang=\"en\" href=\"https://example.test/dit/en/\"/></url></urlset>");
        site.Write("robots.txt", "User-agent: *\nSitemap: https://example.test/dit/sitemap.xml\n");
        site.WriteManifests(["css/site.abcdefghij.css", "fonts/x.abcdefghij.woff2", "img/logo.0123456789.png", "js/app.abcdefghij.js"]);
        return site;
    }

    public IReadOnlyList<string> Verify(Uri? siteUrl = null) => ExportVerifier.Verify(Root, siteUrl ?? TestPages.Site);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is harmless.
        }
    }
}

/// <summary>An in-memory app: answers by path, records every request, and can fail or delay on demand.</summary>
internal sealed class FakeApp : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Func<int, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);
    private int _inFlight;
    private int _maxInFlight;

    public ConcurrentQueue<Uri> Requests { get; } = new();

    public ConcurrentQueue<string> AcceptEncodings { get; } = new();

    public TimeSpan Latency { get; set; }

    public int MaxInFlight => _maxInFlight;

    public int CountOf(string path) => _counts.GetValueOrDefault(path);

    public void Add(string path, string body, string contentType = "text/html") =>
        Add(path, Encoding.UTF8.GetBytes(body), contentType);

    public void Add(string path, byte[] body, string contentType = "application/octet-stream") =>
        _routes[path] = _ => Bytes(HttpStatusCode.OK, body, contentType);

    public void Add(string path, Func<int, HttpResponseMessage> response) => _routes[path] = response;

    public void AddStatus(string path, HttpStatusCode status, string? location = null) =>
        _routes[path] = _ => Redirect(status, location);

    public static HttpResponseMessage Bytes(HttpStatusCode status, byte[] body, string contentType)
    {
        HttpResponseMessage response = new(status) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return response;
    }

    public static HttpResponseMessage Redirect(HttpStatusCode status, string? location)
    {
        HttpResponseMessage response = new(status) { Content = new ByteArrayContent([]) };
        if (location is not null)
        {
            response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        }

        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Uri uri = request.RequestUri!;
        Requests.Enqueue(uri);
        AcceptEncodings.Enqueue(string.Join(",", request.Headers.AcceptEncoding));
        int now = Interlocked.Increment(ref _inFlight);
        int seen;
        while (now > (seen = _maxInFlight))
        {
            Interlocked.CompareExchange(ref _maxInFlight, now, seen);
        }

        try
        {
            if (Latency > TimeSpan.Zero)
            {
                await Task.Delay(Latency, cancellationToken);
            }

            int count = _counts.AddOrUpdate(uri.AbsolutePath, 1, (_, c) => c + 1);
            return _routes.TryGetValue(uri.AbsolutePath, out Func<int, HttpResponseMessage>? route)
                ? route(count)
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) };
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}

/// <summary>A site the exporter can crawl: the manifest, pages, assets, content shards and a redirect, all on one fake app.</summary>
internal sealed class ExportFixture
{
    public const string Prefix = "/dit";

    public static readonly Uri App = new("http://app.test:5080/dit");

    public ExportFixture(bool brotli = false, string questionId = "q1")
    {
        byte[] question = Encoding.UTF8.GetBytes("{\"id\":\"q1\"}");
        QuestionHash = TestPages.Digest(question);
        QuestionPath = $"data/en/q/{questionId}.{QuestionHash}.json";
        StaticIndexDto index = new(
            StaticIndexDto.CurrentFormat,
            "en",
            "dotnet",
            "content",
            [],
            [new StaticIndexEntryDto(questionId, "slug", "topic", "junior", "single", "T", QuestionHash)]);
        byte[] indexBytes = JsonSerializer.SerializeToUtf8Bytes(index, TrainerJsonContext.Default.StaticIndexDto);
        IndexPath = $"data/en/dotnet/index.{TestPages.Digest(indexBytes)}.json";

        List<string> assets =
        [
            "/dit/css/site.abcdefghij.css",
            "/dit/css/site.css",
            "/dit/_framework/a.abcdefghij.wasm",
            "/dit/js/app.abcdefghij.js",
        ];
        List<string> immutable = ["/dit/css/site.abcdefghij.css", "/dit/_framework/a.abcdefghij.wasm", "/dit/js/app.abcdefghij.js"];
        if (brotli)
        {
            assets.Add("/dit/_framework/a.abcdefghij.wasm.br");
            immutable.Add("/dit/_framework/a.abcdefghij.wasm.br");
            assets.Add("/dit/" + BootTableRoute);
            immutable.Add("/dit/" + BootTableRoute);
        }

        Manifest = new ExportManifest(
            Pages: ["/dit/", "/dit/en/", "/dit/en/dotnet/practice/", "/dit/en/dotnet/questions/new/"],
            Redirects: ["/dit/en/dotnet/questions/old/"],
            Files: ["/dit/sitemap.xml", "/dit/robots.txt", "/dit/sw.js"],
            Indexes: ["/dit/" + IndexPath],
            Assets: [.. assets],
            Immutable: [.. immutable],
            NotFoundPage: "/dit/not-found",
            RedirectPageCsp: TestPages.Csp,
            BuildId: TestPages.BuildId,
            EligiblePattern: TestPages.EligiblePattern);

        string css = "@font-face{src:url(\"../fonts/x.abcdefghij.woff2\")}";
        Handler.Add("/dit/static-export/manifest.json", JsonSerializer.Serialize(Manifest, ExportJsonContext.Default.ExportManifest), "application/json");
        Handler.Add("/dit/", TestPages.Html("<a href=\"/dit/en/\">English</a>"));
        Handler.Add("/dit/en/", TestPages.Html("<a href=\"/dit/en/dotnet/practice/\">Practice</a>"));
        Handler.Add(
            "/dit/en/dotnet/practice/",
            TestPages.Html("<p>Practice</p>", "<meta property=\"og:image\" content=\"https://example.test/dit/og/q1.abcdefghij.png\">"));
        Handler.Add("/dit/en/dotnet/questions/new/", TestPages.Html("<p>New</p>"));
        Handler.AddStatus("/dit/en/dotnet/questions/old/", HttpStatusCode.MovedPermanently, "/dit/en/dotnet/questions/new/");
        Handler.Add("/dit/not-found", (_) => FakeApp.Bytes(HttpStatusCode.NotFound, Encoding.UTF8.GetBytes(TestPages.Html("<p>Not found</p>")), "text/html"));
        Handler.Add("/dit/sitemap.xml", "<?xml version=\"1.0\"?><urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\"><url><loc>https://example.test/dit/en/</loc></url></urlset>", "application/xml");
        Handler.Add("/dit/robots.txt", "User-agent: *\nSitemap: https://example.test/dit/sitemap.xml\n", "text/plain");
        Handler.Add("/dit/sw.js", "self.addEventListener('fetch',()=>{});", "text/javascript");
        Handler.Add("/dit/css/site.abcdefghij.css", css, "text/css");
        Handler.Add("/dit/css/site.css", css, "text/css");
        Handler.Add("/dit/_framework/a.abcdefghij.wasm", WasmBytes, "application/wasm");
        Handler.Add("/dit/js/app.abcdefghij.js", "console.log(1);", "text/javascript");
        Handler.Add("/dit/og/q1.abcdefghij.png", [137, 80, 78, 71], "image/png");
        Handler.Add("/dit/" + IndexPath, indexBytes, "application/json");
        Handler.Add("/dit/" + QuestionPath, question, "application/json");
        if (brotli)
        {
            Handler.Add("/dit/_framework/a.abcdefghij.wasm.br", BrotliOf(WasmBytes), "application/octet-stream");
            Handler.Add("/dit/" + BootTableRoute, BootTableText, "text/javascript");
        }
    }

    /// <summary>The bytes the app serves for <c>_framework/a.abcdefghij.wasm</c>.</summary>
    public static byte[] WasmBytes { get; } = [0, 97, 115, 109];

    /// <summary>The integrity table of the fixture's Brotli runtime, as the app writes it, and its content-addressed route.</summary>
    public static string BootTableText { get; } =
        "export default {" + (char)34 + "a.abcdefghij.wasm" + (char)34 + ":" + (char)34 + "sha256-" + Convert.ToBase64String(SHA256.HashData(WasmBytes)) + (char)34 + "};" + (char)10;

    public static string BootTableRoute { get; } =
        "js/boot-integrity." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(BootTableText)))[..10] + ".mjs";

    /// <summary>A Brotli stream of <paramref name="bytes"/>, the shape of a published <c>.br</c> file.</summary>
    public static byte[] BrotliOf(byte[] bytes)
    {
        using MemoryStream output = new();
        using (System.IO.Compression.BrotliStream brotli = new(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            brotli.Write(bytes);
        }

        return output.ToArray();
    }

    public FakeApp Handler { get; } = new();

    public ExportManifest Manifest { get; }

    public string QuestionHash { get; }

    public string QuestionPath { get; }

    public string IndexPath { get; }

    public static ExportOptions Options(string outDir, string buildId = TestPages.BuildId) =>
        new(App, TestPages.Site, outDir, buildId, "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef");

    public SiteExporter Exporter() => new(Handler, TimeProvider.System, (_, _) => Task.CompletedTask);

    /// <summary>Replaces the manifest the app serves.</summary>
    public void Serve(ExportManifest manifest) =>
        Handler.Add("/dit/static-export/manifest.json", JsonSerializer.Serialize(manifest, ExportJsonContext.Default.ExportManifest), "application/json");
}
