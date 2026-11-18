using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Playwright;
using Trainer.StaticSite;

namespace Trainer.Web.E2E;

/// <summary>
/// Boot resources decoded in the page (ADR 0027): GitHub Pages serves WebAssembly gzip at best, so the static edition fetches
/// the published <c>.br</c> files and decodes them with a vendored decoder. The runtime hands the loader an empty integrity for
/// every fingerprinted resource, so the export writes <c>js/boot-integrity.{hash}.mjs</c> (name to sha256 of the published
/// file) and the loader verifies the decoded bytes against it. The fixture runs a Debug build and writes the <c>.br</c> files
/// itself, but the table comes from the app's endpoint manifest like a Release build's, so a real boot goes through the decode
/// path; the failure paths are driven through the same <c>loadBootResource</c> function the page hands to Blazor.
/// </summary>
[Trait("Category", "E2E")]
public sealed class StaticBrotliBootTests(StaticSiteFixture site) : IClassFixture<StaticSiteFixture>, IAsyncLifetime
{
    private const string PracticePath = "en/dotnet/practice/";

    // Blazor.start receives the loader as webAssembly.loadBootResource; keep the options the page passes so a test can call it.
    // blazor.web.js assigns window.Blazor first and adds start() afterwards, so start is wrapped when the page first reads it.
    private const string CaptureStartOptions =
        """
        (function () {
          var blazor;
          Object.defineProperty(window, 'Blazor', {
            configurable: true,
            get: function () {
              if (blazor && typeof blazor.start === 'function' && !blazor.__ditWrapped) {
                var start = blazor.start;
                blazor.__ditWrapped = true;
                blazor.start = function (options) { window.__ditBootOptions = options; return start.apply(this, arguments); };
              }

              return blazor;
            },
            set: function (value) { blazor = value; }
          });
        })();
        """;

    private static float Wait => StaticSiteSteps.IslandTimeoutMs;

    public ValueTask InitializeAsync() => new(site.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class LoaderResult
    {
        public string? ContentType { get; set; }

        public string? Url { get; set; }

        public long Length { get; set; }

        public string? Sha { get; set; }
    }

    private async Task<(IBrowserContext Context, IPage Tab, List<string> Warnings)> OpenAsync(string relativeUrl = PracticePath)
    {
        IBrowserContext context = await site.NewContextAsync(serviceWorkers: ServiceWorkerPolicy.Block);
        await context.AddInitScriptAsync(CaptureStartOptions);
        IPage tab = await site.NewPageAsync(context);
        List<string> warnings = [];
        tab.Console += (_, message) =>
        {
            if (message.Type == "warning")
            {
                lock (warnings)
                {
                    warnings.Add(message.Text);
                }
            }
        };
        await tab.GotoAsync(site.Url(relativeUrl).ToString());
        await StaticSiteSteps.WaitForIslandAsync(tab);
        return (context, tab, warnings);
    }

    private static async Task<LoaderResult> LoadAsync(IPage tab, string type, string name, string uri, string integrity) =>
        await tab.EvaluateAsync<LoaderResult>(
            """
            async ([type, name, uri, integrity]) => {
              const response = await window.__ditBootOptions.webAssembly.loadBootResource(type, name, uri, integrity);
              const bytes = new Uint8Array(await response.arrayBuffer());
              const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', bytes));
              return { contentType: response.headers.get('content-type'), url: response.url, length: bytes.length, sha: btoa(String.fromCharCode.apply(null, digest)) };
            }
            """,
            new object[] { type, name, uri, integrity });

    private string Sha(string relative) => Convert.ToBase64String(SHA256.HashData(File.ReadAllBytes(site.FirstBuild.FullPath(relative))));

    private string ClientAssembly() => StaticSiteSteps.FingerprintedFile(site.FirstBuild, "_framework", "Trainer.Web.Client", ".wasm");

    [Fact]
    public async Task Brotli_DecodesTheCompressedFileAndChecksItsIntegrity()
    {
        (IBrowserContext context, IPage tab, List<string> warnings) = await OpenAsync();
        await using IBrowserContext owned = context;
        string assembly = ClientAssembly();
        string native = StaticSiteSteps.FingerprintedFile(site.FirstBuild, "_framework", "dotnet.native", ".wasm");

        LoaderResult decoded = await LoadAsync(tab, "assembly", Path.GetFileName(assembly), site.Url(assembly).ToString(), "sha256-" + Sha(assembly));
        LoaderResult wasm = await LoadAsync(tab, "dotnetwasm", Path.GetFileName(native), site.Url(native).ToString(), "sha256-" + Sha(native));

        // A Response built from the decoded bytes has no URL of its own; the fallback is a fetch and has one.
        Assert.Equal(string.Empty, decoded.Url);
        Assert.Equal(Sha(assembly), decoded.Sha);
        Assert.Equal(new FileInfo(site.FirstBuild.FullPath(assembly)).Length, decoded.Length);
        Assert.Equal("application/octet-stream", decoded.ContentType);
        Assert.Equal(string.Empty, wasm.Url);
        Assert.Equal(Sha(native), wasm.Sha);
        Assert.Equal("application/wasm", wasm.ContentType);
        lock (warnings)
        {
            Assert.DoesNotContain(warnings, text => text.Contains(Path.GetFileName(assembly), StringComparison.Ordinal) && text.Contains("uncompressed", StringComparison.Ordinal) && text.Contains("integrity value", StringComparison.Ordinal));
        }

        site.AssertClean(tab);
    }

    [Fact]
    public async Task Brotli_BootsOnANestedRoute()
    {
        (IBrowserContext context, IPage tab, List<string> warnings) = await OpenAsync("ru/dotnet/practice/");
        await using IBrowserContext owned = context;
        string native = StaticSiteSteps.FingerprintedFile(site.FirstBuild, "_framework", "dotnet.native", ".wasm");

        // The page two levels below the prefix asked for the compressed runtime under the prefix and got it.
        Assert.Contains(site.OriginRequests, request => request.PathAndQuery == StaticSiteFixture.Prefix + native + ".br" && request.Status == 200);
        Assert.Equal(1, await tab.Locator(".island-root[data-island-culture='ru']").CountAsync());
        Assert.Equal(0, await tab.Locator(".island-state").CountAsync());

        // The boot really used the compressed files: a loader that refuses every decoded file boots from the uncompressed
        // ones and would pass everything above. That includes the satellite assembly of the page's culture (_framework/ru/...).
        lock (warnings)
        {
            string[] fallbacks = [.. warnings.Where(text => text.Contains("Brotli boot resource failed", StringComparison.Ordinal) || text.Contains("uncompressed", StringComparison.Ordinal))];
            Assert.True(fallbacks.Length == 0, string.Join(Environment.NewLine, fallbacks));
        }

        string frameworkPrefix = StaticSiteFixture.Prefix + "_framework/";
        Assert.DoesNotContain(
            site.OriginRequests,
            request => request.PathAndQuery.StartsWith(frameworkPrefix, StringComparison.Ordinal)
                && request.PathAndQuery.EndsWith(".wasm", StringComparison.Ordinal));
        Assert.Contains(site.OriginRequests, request => request.PathAndQuery.StartsWith(frameworkPrefix, StringComparison.Ordinal) && request.PathAndQuery.EndsWith(".wasm.br", StringComparison.Ordinal) && request.Status == 200);
        site.AssertClean(tab);
    }

    [Fact]
    public async Task Brotli_ImportMapKeyIsTheUrlTheLoaderImports()
    {
        (IBrowserContext context, IPage tab, _) = await OpenAsync();
        await using IBrowserContext owned = context;
        string decoder = StaticSiteSteps.FingerprintedFile(site.FirstBuild, "js/vendor", "brotli-decode", ".mjs");

        // The loader says import('./vendor/brotli-decode.mjs') from islands-start.js; the import map keys are resolved against
        // <base href>. Under a path base both must be the same URL, or the map silently does nothing and the unfingerprinted
        // alias (never cached by the worker) is fetched instead.
        System.Text.Json.JsonElement resolved = await tab.EvaluateAsync<System.Text.Json.JsonElement>(
            """
            () => {
              const imports = JSON.parse(document.querySelector('script[type=importmap]').textContent).imports;
              const start = [...document.scripts].find(script => /\/islands-start\.[a-z0-9]{10}\.js$/.test(script.src)).src;
              const wanted = new URL('./vendor/brotli-decode.mjs', start).href;
              const keys = Object.keys(imports).filter(key => new URL(key, document.baseURI).href === wanted);
              return { wanted, keys, target: keys.length === 1 ? new URL(imports[keys[0]], document.baseURI).href : null };
            }
            """);

        Assert.Equal(site.Url("js/vendor/brotli-decode.mjs").ToString(), resolved.GetProperty("wanted").GetString());
        Assert.Equal(1, resolved.GetProperty("keys").GetArrayLength());
        Assert.Equal(site.Url(decoder).ToString(), resolved.GetProperty("target").GetString());
        Assert.Contains(site.OriginRequests, request => request.PathAndQuery == StaticSiteFixture.Prefix + decoder && request.Status == 200);
        Assert.DoesNotContain(site.OriginRequests, request => request.PathAndQuery == StaticSiteFixture.Prefix + "js/vendor/brotli-decode.mjs");
        site.AssertClean(tab);
    }

    [Fact]
    public async Task Brotli_MissingCompressedFile_FallsBack()
    {
        string assembly = ClientAssembly();
        site.FailOnce(assembly + ".br", 404);

        (IBrowserContext context, IPage tab, List<string> warnings) = await OpenAsync();
        await using IBrowserContext owned = context;

        // The island booted although one compressed file answered 404: that file came uncompressed, and the loader said so.
        Assert.Equal(0, await tab.Locator(".island-state").CountAsync());
        string name = Path.GetFileName(assembly);
        lock (warnings)
        {
            Assert.Contains(warnings, text => text.Contains(name + ".br: HTTP 404", StringComparison.Ordinal) && text.Contains("loading " + name + " uncompressed", StringComparison.Ordinal));
        }

        Assert.Contains(site.OriginRequests, request => request.PathAndQuery == StaticSiteFixture.Prefix + assembly + ".br" && request.Status == 404);
        Assert.Contains(site.OriginRequests, request => request.PathAndQuery == StaticSiteFixture.Prefix + assembly && request.Status == 200);
        site.AssertRequestsStayUnderPrefix(tab);
        site.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Brotli_CorruptCompressedFile_FallsBackAfterIntegrityFailure()
    {
        (IBrowserContext context, IPage tab, List<string> warnings) = await OpenAsync();
        await using IBrowserContext owned = context;
        string assembly = ClientAssembly();
        string name = Path.GetFileName(assembly);
        string integrity = "sha256-" + Sha(assembly);
        byte[] original = await File.ReadAllBytesAsync(site.FirstBuild.FullPath(assembly), TestContext.Current.CancellationToken);
        byte[] compressed = await File.ReadAllBytesAsync(site.FirstBuild.FullPath(assembly + ".br"), TestContext.Current.CancellationToken);

        // Cut off in the middle: the decoder cannot finish it.
        await site.WriteToServedAsync(assembly + ".br", compressed[..(compressed.Length / 2)]);
        await StaticSiteFixture.ExpireHttpCacheAsync(tab);
        LoaderResult truncated = await LoadAsync(tab, "assembly", name, site.Url(assembly).ToString(), integrity);

        Assert.Equal(Sha(assembly), truncated.Sha);
        Assert.Equal(original.Length, truncated.Length);
        Assert.Equal(site.Url(assembly).ToString(), truncated.Url);
        lock (warnings)
        {
            Assert.Contains(warnings, text => text.Contains("loading " + name + " uncompressed", StringComparison.Ordinal) && !text.Contains("HTTP", StringComparison.Ordinal));
            warnings.Clear();
        }

        // A well-formed stream that decodes to other bytes: only the integrity check can tell.
        byte[] other = [.. original];
        other[original.Length / 2] ^= 0xFF;
        await site.WriteToServedAsync(assembly + ".br", Compress(other));
        await StaticSiteFixture.ExpireHttpCacheAsync(tab);
        LoaderResult wrong = await LoadAsync(tab, "assembly", name, site.Url(assembly).ToString(), integrity);

        Assert.Equal(Sha(assembly), wrong.Sha);
        Assert.Equal(site.Url(assembly).ToString(), wrong.Url);
        lock (warnings)
        {
            Assert.Contains(warnings, text => text.Contains(name + ": decoded bytes do not match the integrity value", StringComparison.Ordinal));
        }

        site.AssertClean(tab);
    }

    [Fact]
    public async Task Brotli_BootsOnAHostThatDoesNotCompress()
    {
        // Pages compresses WebAssembly, JavaScript and JSON with gzip; a host that compresses nothing must work as well.
        await using PagesEmulator plain = await PagesEmulator.StartAsync(new PagesEmulatorOptions(site.Directory, site.BaseAddress, Gzip: false));
        await using IBrowserContext context = await site.NewContextAsync(serviceWorkers: ServiceWorkerPolicy.Block);
        IPage tab = await site.NewPageAsync(context);

        await tab.GotoAsync(new Uri(plain.BaseAddress, PracticePath).ToString());
        await tab.Locator(StaticSiteSteps.ReadyIsland).WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });

        Assert.Equal(0, await tab.Locator(".island-state").CountAsync());
        Assert.Equal(1, await tab.Locator(".island-root").CountAsync());
        site.AssertRequestsStayUnderPrefix(tab, plain.BaseAddress);
        site.AssertNoCspViolations(tab);
    }

    private static byte[] Compress(byte[] bytes)
    {
        using MemoryStream output = new();
        using (BrotliStream brotli = new(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            brotli.Write(bytes);
        }

        return output.ToArray();
    }
}
