using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Trainer.Application.StaticSite;
using Trainer.StaticSite;
using Trainer.TestSupport;
using Trainer.Web.Seo;
using Trainer.Web.Startup;
using Trainer.Web.StaticSite;

namespace Trainer.Web.Tests;

/// <summary>One real export of the fixture site: the static-edition app, crawled in process, written to a directory.</summary>
public sealed class ExportedSite(TrainerWebFactory factory, Uri siteUrl, string directory)
{
    public TrainerWebFactory Factory { get; } = factory;

    public Uri SiteUrl { get; } = siteUrl;

    public string Directory { get; } = directory;

    public string PathBase { get; } = Uri.UnescapeDataString(siteUrl.AbsolutePath).TrimEnd('/');

    public string FullPath(string relative) => Path.Combine(Directory, relative.Replace('/', Path.DirectorySeparatorChar));
}

/// <summary>Exports the fixture site once per site URL for the whole test class.</summary>
public sealed class ExportedSites(PostgresFixture postgres) : IAsyncLifetime
{
    public const string BuildId = "fixture123.42.1";

    public const string GoatCounterCode = "export-test";

    private readonly ConcurrentDictionary<string, Lazy<Task<ExportedSite>>> _sites = new(StringComparer.Ordinal);

    public Task<ExportedSite> GetAsync(string siteUrl) =>
        _sites.GetOrAdd(siteUrl, url => new Lazy<Task<ExportedSite>>(() => ExportAsync(new Uri(url)))).Value;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (Lazy<Task<ExportedSite>> lazy in _sites.Values)
        {
            if (!lazy.IsValueCreated)
            {
                continue;
            }

            if (lazy.Value.IsFaulted)
            {
                // The failed export already cleaned up after itself; the test that asked for it reports the failure.
                continue;
            }

            ExportedSite site = await lazy.Value;
            await site.Factory.DisposeAsync();
            System.IO.Directory.Delete(site.Directory, recursive: true);
        }
    }

    private async Task<ExportedSite> ExportAsync(Uri siteUrl)
    {
        TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(
            postgres,
            new TrainerWebFactoryOptions
            {
                Settings = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["App:Edition"] = "Static",
                    ["App:PublicBaseUrl"] = siteUrl.ToString(),
                    ["App:StaticSite:BuildId"] = BuildId,
                    ["App:Analytics:GoatCounterCode"] = GoatCounterCode,
                    ["App:StaticSite:GoogleSiteVerification"] = "google-token-123",
                    ["App:StaticSite:YandexVerification"] = "yandex-token-456",
                },
            });
        await factory.WaitUntilReadyAsync();

        string directory = Path.Combine(Path.GetTempPath(), "dit-fixture-export-" + Guid.NewGuid().ToString("N"));
        string pathBase = Uri.UnescapeDataString(siteUrl.AbsolutePath).TrimEnd('/');
        Uri app = new("https://localhost" + pathBase);
        try
        {
            SiteExporter exporter = new(factory.Server.CreateHandler(), TimeProvider.System, (span, ct) => Task.Delay(span, ct));
            await exporter.ExportAsync(new ExportOptions(app, siteUrl, directory, BuildId, "0123456789abcdef0123456789abcdef01234567"), CancellationToken.None);
            return new ExportedSite(factory, siteUrl, directory);
        }
        catch
        {
            await factory.DisposeAsync();
            if (System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }

            throw;
        }
    }
}

/// <summary>The static export of the real application (task 16): crawl, verification, manifest and emulator.</summary>
public sealed class StaticExportIntegrationTests(ExportedSites sites, PostgresFixture postgres) : IClassFixture<ExportedSites>
{
    private const string PrefixedSite = "https://example.test/dev-interview-trainer/";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("https://example.test/dev-interview-trainer/")]
    [InlineData("https://example.test/")]
    public async Task ExportOfTheFixtureSite_VerifiesClean(string siteUrl)
    {
        ExportedSite site = await sites.GetAsync(siteUrl);

        IReadOnlyList<string> problems = ExportVerifier.Verify(site.Directory, site.SiteUrl);

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Take(40)));
        foreach (string file in new[]
        {
            "index.html", "404.html", "robots.txt", "sitemap.xml", "sitemap-en.xml", "sitemap-ru.xml", "sw.js", "build.json", "immutable.json",
            "en/index.html", "ru/index.html", "en/about/index.html", "en/dotnet/index.html", "en/dotnet/questions/index.html",
            "en/dotnet/practice/index.html", "ru/dotnet/mock/index.html", "ru/dotnet/review/index.html", "ru/dotnet/profile/index.html",
        })
        {
            Assert.True(File.Exists(site.FullPath(file)), file + " is missing from the export");
        }

        ImmutableInfo immutable = JsonSerializer.Deserialize(File.ReadAllBytes(site.FullPath("immutable.json")), ExportJsonContext.Default.ImmutableInfo)!;
        Assert.Equal(ExportedSites.BuildId, immutable.BuildId);
        Assert.Equal(StaticAssetRules.JavaScriptRegex, immutable.EligiblePattern);
        Assert.Contains(immutable.Files, f => f.StartsWith("_framework/dotnet.native.", StringComparison.Ordinal) && f.EndsWith(".wasm", StringComparison.Ordinal));
        Assert.Contains(immutable.Files, f => f.StartsWith("css/site.", StringComparison.Ordinal));
        Assert.Contains(immutable.Files, f => f.StartsWith("data/en/dotnet/index.", StringComparison.Ordinal));
        Assert.Contains(immutable.Files, f => f.StartsWith("data/en/q/", StringComparison.Ordinal));
        Assert.Contains(immutable.Files, f => f.StartsWith("og/", StringComparison.Ordinal));
        Assert.All(immutable.Files, file => Assert.True(File.Exists(site.FullPath(file)), file));
        Assert.DoesNotContain("sw.js", immutable.Files);
        Assert.DoesNotContain("css/site.css", immutable.Files);
        Assert.DoesNotContain(immutable.Files, f => f.EndsWith(".html", StringComparison.Ordinal));
        BuildInfo build = JsonSerializer.Deserialize(File.ReadAllBytes(site.FullPath("build.json")), ExportJsonContext.Default.BuildInfo)!;
        Assert.Equal(ExportedSites.BuildId, build.BuildId);
        Assert.Equal("google-token-123", build.GoogleSiteVerification);
        Assert.Equal("yandex-token-456", build.YandexVerification);
        // The verifier held the pages to those values: front pages carry them, no other page does.
        Assert.Contains("google-token-123", File.ReadAllText(site.FullPath("index.html")), StringComparison.Ordinal);
        Assert.Contains("yandex-token-456", File.ReadAllText(site.FullPath("ru/index.html")), StringComparison.Ordinal);
        Assert.DoesNotContain("google-token-123", File.ReadAllText(site.FullPath("en/about/index.html")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verifier_CatchesABrokenLinkOnARealExportedPage()
    {
        ExportedSite site = await sites.GetAsync(PrefixedSite);
        string page = site.FullPath("en/about/index.html");
        string original = await File.ReadAllTextAsync(page, Ct);
        try
        {
            await File.WriteAllTextAsync(page, original.Replace("</body>", "<a href=\"/ru/\">escapes</a></body>", StringComparison.Ordinal), Ct);

            IReadOnlyList<string> problems = ExportVerifier.Verify(site.Directory, site.SiteUrl);

            string problem = Assert.Single(problems);
            Assert.StartsWith("en/about/index.html:", problem, StringComparison.Ordinal);
        }
        finally
        {
            await File.WriteAllTextAsync(page, original, Ct);
        }
    }

    [Fact]
    public async Task Verifier_CatchesAnEscapingUrlInTheIslandBootstrapOfARealPage()
    {
        ExportedSite site = await sites.GetAsync(PrefixedSite);
        string path = site.FullPath("en/dotnet/practice/index.html");
        string original = await File.ReadAllTextAsync(path, Ct);
        Match parameters = Regex.Match(original, "\"parameterValues\":\"(?<value>[^\"]+)\"");
        Assert.True(parameters.Success, "the island marker carries its bootstrap");
        string bootstrap = Encoding.UTF8.GetString(Convert.FromBase64String(parameters.Groups["value"].Value));
        Assert.Contains("data/en/dotnet/index.", bootstrap, StringComparison.Ordinal);
        string tampered = Convert.ToBase64String(Encoding.UTF8.GetBytes(bootstrap.Replace("data/en/dotnet/index.", "/data/en/dotnet/index.", StringComparison.Ordinal)));
        try
        {
            await File.WriteAllTextAsync(path, original.Replace(parameters.Groups["value"].Value, tampered, StringComparison.Ordinal), Ct);

            IReadOnlyList<string> problems = ExportVerifier.Verify(site.Directory, site.SiteUrl);

            string problem = Assert.Single(problems);
            Assert.StartsWith("en/dotnet/practice/index.html:", problem, StringComparison.Ordinal);
            Assert.Contains("persisted state", problem, StringComparison.Ordinal);
        }
        finally
        {
            await File.WriteAllTextAsync(path, original, Ct);
        }
    }

    [Fact]
    public async Task ExportedPages_NameTheirCardsByThePublicUrlAndTheCardsAreSaved()
    {
        ExportedSite site = await sites.GetAsync(PrefixedSite);

        string page = File.ReadAllText(site.FullPath("en/dotnet/index.html"));

        Assert.Contains(PrefixedSite + "og/", page, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost", page, StringComparison.Ordinal);
        Assert.NotEmpty(Directory.EnumerateFiles(site.FullPath("og"), "*.png", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Manifest_IsNotMappedInServerEdition()
    {
        await using TrainerWebFactory server = await TrainerWebFactory.CreateAsync(postgres);
        await server.WaitUntilReadyAsync();
        using HttpClient client = server.CreateNonRedirectingClient();

        using HttpResponseMessage response = await client.GetAsync("/static-export/manifest.json", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Manifest_SetsNoCookie()
    {
        ExportedSite site = await sites.GetAsync(PrefixedSite);
        using HttpClient client = site.Factory.CreateNonRedirectingClient();

        using HttpResponseMessage response = await client.GetAsync("/dev-interview-trainer/static-export/manifest.json", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Manifest_ListsEverythingUnderThePrefixWithTheServicesOwnRules()
    {
        ExportedSite site = await sites.GetAsync(PrefixedSite);
        using HttpClient client = site.Factory.CreateNonRedirectingClient();
        ExportManifest manifest = (await client.GetFromJsonAsync("/dev-interview-trainer/static-export/manifest.json", ExportJsonContext.Default.ExportManifest, Ct))!;

        string[] all = [.. manifest.Pages, .. manifest.Redirects, .. manifest.Files, .. manifest.Indexes, .. manifest.Assets, .. manifest.Immutable, manifest.NotFoundPage];
        Assert.All(all, path => Assert.StartsWith("/dev-interview-trainer/", path, StringComparison.Ordinal));
        Assert.Contains("/dev-interview-trainer/", manifest.Pages);
        Assert.Contains("/dev-interview-trainer/en/dotnet/practice/", manifest.Pages);
        Assert.Contains("/dev-interview-trainer/ru/about/", manifest.Pages);
        Assert.Contains("/dev-interview-trainer/sw.js", manifest.Files);
        Assert.Contains("/dev-interview-trainer/sitemap-ru.xml", manifest.Files);
        Assert.Equal("/dev-interview-trainer/not-found", manifest.NotFoundPage);
        Assert.Equal(ExportedSites.BuildId, manifest.BuildId);
        Assert.Equal(StaticAssetRules.JavaScriptRegex, manifest.EligiblePattern);
        Assert.Equal("google-token-123", manifest.GoogleSiteVerification);
        Assert.Equal("yandex-token-456", manifest.YandexVerification);
        Assert.All(manifest.Immutable, path => Assert.Contains(path, manifest.Assets));
        Assert.DoesNotContain(manifest.Assets, path => path.EndsWith(".gz", StringComparison.Ordinal));
        Assert.Equal(manifest.Pages.Order(StringComparer.Ordinal), manifest.Pages);

        // The redirect pages carry the page policy of the app, allowing the configured GoatCounter site.
        Assert.Contains($"https://{ExportedSites.GoatCounterCode}.goatcounter.com", manifest.RedirectPageCsp, StringComparison.Ordinal);
        Assert.Contains("base-uri 'none'", manifest.RedirectPageCsp, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_BrotliRuntimeRoutesAreExportedAndImmutableOnlyWhenEnabled()
    {
        string name = "dit-export-brotli-" + Guid.NewGuid().ToString("N");
        string path = Path.Combine(Path.GetTempPath(), name + ".staticwebassets.endpoints.json");
        File.WriteAllText(path, "{\"Version\":1,\"ManifestType\":\"Build\",\"Endpoints\":[" + string.Join(',',
            Endpoint("_framework/a.abcdefghij.wasm", "_framework/a.wasm"),
            IntegrityOnly("_framework/a.abcdefghij.wasm.br"),
            IntegrityOnly("_framework/a.abcdefghij.wasm.gz"),
            Endpoint("css/site.abcdefghij.css", "css/site.css")) + "]}");
        try
        {
            SitePaths paths = new(Options.Create(new AppOptions { Edition = SiteEdition.Static, PublicBaseUrl = "https://example.test/dit/" }));
            StaticPagePaths pages = new(["/en"], []);

            StaticExportManifest on = StaticExportManifest.Create(paths, pages, [], StaticAssetList.FromManifest(path, keepBrotliRuntime: true), "b.1.1", null);
            StaticExportManifest off = StaticExportManifest.Create(paths, pages, [], StaticAssetList.FromManifest(path, keepBrotliRuntime: false), "b.1.1", null);

            Assert.Contains("/dit/_framework/a.abcdefghij.wasm.br", on.Assets);
            Assert.Contains("/dit/_framework/a.abcdefghij.wasm.br", on.Immutable);
            Assert.True(StaticAssetRules.IsEligible("_framework/a.abcdefghij.wasm.br"));
            Assert.DoesNotContain(on.Assets, route => route.EndsWith(".gz", StringComparison.Ordinal));
            Assert.DoesNotContain(off.Assets, route => route.EndsWith(".br", StringComparison.Ordinal));
            Assert.DoesNotContain(off.Immutable, route => route.EndsWith(".br", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExportEnvironment_TurnsTheBrotliRuntimeOn()
    {
        // The export runs the app in the StaticExport environment, which is where the Pages configuration lives.
        await using TrainerWebFactory export = await TrainerWebFactory.CreateAsync(
            postgres,
            new TrainerWebFactoryOptions
            {
                Environment = EditionStartupCheck.StaticExportEnvironment,
                Settings = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["App:Edition"] = "Static",
                    ["App:PublicBaseUrl"] = "https://example.test/dit/",
                },
            });
        await export.WaitUntilReadyAsync();
        await using TrainerWebFactory testing = await TrainerWebFactory.CreateAsync(
            postgres,
            new TrainerWebFactoryOptions
            {
                Settings = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["App:Edition"] = "Static",
                    ["App:PublicBaseUrl"] = "https://example.test/dit/",
                },
            });
        await testing.WaitUntilReadyAsync();

        Assert.True(export.Services.GetRequiredService<IOptions<AppOptions>>().Value.StaticSite.BrotliRuntime);
        Assert.False(testing.Services.GetRequiredService<IOptions<AppOptions>>().Value.StaticSite.BrotliRuntime);
    }

    [Fact]
    public async Task Emulator_FailOnceThenServes()
    {
        ExportedSite site = await sites.GetAsync(PrefixedSite);
        await using PagesEmulator emulator = await PagesEmulator.StartAsync(new PagesEmulatorOptions(site.Directory, site.SiteUrl));
        using HttpClient client = NewClient();
        emulator.FailOnce("/dev-interview-trainer/en/about/", 503);
        Uri about = new(emulator.BaseAddress, "en/about/");

        using HttpResponseMessage first = await client.GetAsync(about, Ct);
        using HttpResponseMessage second = await client.GetAsync(about, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(await File.ReadAllTextAsync(site.FullPath("en/about/index.html"), Ct), await second.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Emulator_DelaysMatchingPaths()
    {
        ExportedSite site = await sites.GetAsync(PrefixedSite);
        await using PagesEmulator emulator = await PagesEmulator.StartAsync(new PagesEmulatorOptions(site.Directory, site.SiteUrl));
        using HttpClient client = NewClient();
        emulator.Delay("/dev-interview-trainer/data/", TimeSpan.FromSeconds(2));
        string index = Directory.EnumerateFiles(site.FullPath("data/en/dotnet"), "index.*.json").Select(Path.GetFileName).Single()!;

        Stopwatch indexTime = Stopwatch.StartNew();
        using HttpResponseMessage indexResponse = await client.GetAsync(new Uri(emulator.BaseAddress, "data/en/dotnet/" + index), Ct);
        indexTime.Stop();
        Stopwatch pageTime = Stopwatch.StartNew();
        using HttpResponseMessage page = await client.GetAsync(new Uri(emulator.BaseAddress, "en/about/"), Ct);
        pageTime.Stop();

        Assert.Equal(HttpStatusCode.OK, indexResponse.StatusCode);
        Assert.True(indexTime.Elapsed >= TimeSpan.FromSeconds(1.9), $"the index answered after {indexTime.Elapsed}");
        Assert.True(pageTime.Elapsed < TimeSpan.FromSeconds(1.5), $"the page answered after {pageTime.Elapsed}");
    }

    [Fact]
    public async Task Emulator_SwapRootServesTheNewDirectory()
    {
        ExportedSite site = await sites.GetAsync(PrefixedSite);
        string next = Path.Combine(Path.GetTempPath(), "dit-swap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(next, "en", "about"));
        await File.WriteAllTextAsync(Path.Combine(next, "en", "about", "index.html"), "<html>the next deployment</html>", Ct);
        try
        {
            await using PagesEmulator emulator = await PagesEmulator.StartAsync(new PagesEmulatorOptions(site.Directory, site.SiteUrl));
            using HttpClient client = NewClient();
            Uri about = new(emulator.BaseAddress, "en/about/");
            Assert.Contains("<html", await client.GetStringAsync(about, Ct), StringComparison.Ordinal);

            emulator.SwapRoot(next);

            Assert.Equal("<html>the next deployment</html>", await client.GetStringAsync(about, Ct));
        }
        finally
        {
            Directory.Delete(next, recursive: true);
        }
    }

    [Fact]
    public async Task Emulator_MapsAnExtraFile()
    {
        ExportedSite site = await sites.GetAsync(PrefixedSite);
        string extra = Path.Combine(Path.GetTempPath(), "dit-previous-" + Guid.NewGuid().ToString("N") + ".html");
        await File.WriteAllTextAsync(extra, "<html>the previous build's page</html>", Ct);
        try
        {
            await using PagesEmulator emulator = await PagesEmulator.StartAsync(new PagesEmulatorOptions(site.Directory, site.SiteUrl));
            using HttpClient client = NewClient();
            emulator.MapExtra("/dev-interview-trainer/__previous/", extra);

            Assert.Equal("<html>the previous build's page</html>", await client.GetStringAsync(new Uri(emulator.BaseAddress, "__previous/"), Ct));
        }
        finally
        {
            File.Delete(extra);
        }
    }

    [Fact]
    public async Task Emulator_GzipsWasmOnlyWhenOn()
    {
        ExportedSite site = await sites.GetAsync(PrefixedSite);
        JsonElement immutable = JsonDocument.Parse(await File.ReadAllTextAsync(site.FullPath("immutable.json"), Ct)).RootElement.GetProperty("files");
        string wasm = immutable.EnumerateArray().Select(e => e.GetString()!).First(f => f.StartsWith("_framework/dotnet.native.", StringComparison.Ordinal) && f.EndsWith(".wasm", StringComparison.Ordinal));
        using HttpClient client = NewClient();

        await using PagesEmulator on = await PagesEmulator.StartAsync(new PagesEmulatorOptions(site.Directory, site.SiteUrl, Gzip: true));
        await using PagesEmulator off = await PagesEmulator.StartAsync(new PagesEmulatorOptions(site.Directory, site.SiteUrl, Gzip: false));
        using HttpResponseMessage compressed = await Get(client, on, wasm);
        using HttpResponseMessage plain = await Get(client, off, wasm);

        Assert.Contains("gzip", compressed.Content.Headers.ContentEncoding);
        Assert.Equal("application/wasm", compressed.Content.Headers.ContentType?.MediaType);
        Assert.Empty(plain.Content.Headers.ContentEncoding);
        Assert.True(compressed.Content.Headers.ContentLength < plain.Content.Headers.ContentLength);
        Assert.Equal(new FileInfo(site.FullPath(wasm)).Length, plain.Content.Headers.ContentLength);
    }

    private static async Task<HttpResponseMessage> Get(HttpClient client, PagesEmulator emulator, string relative)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(emulator.BaseAddress, relative));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        return await client.SendAsync(request, Ct);
    }

    private static HttpClient NewClient() =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None });

    private static string Endpoint(string route, string label) =>
        $"{{\"Route\":\"{route}\",\"AssetFile\":\"{route}\",\"Selectors\":[],\"ResponseHeaders\":[],\"EndpointProperties\":[{{\"Name\":\"fingerprint\",\"Value\":\"abcdefghij\"}},{{\"Name\":\"label\",\"Value\":\"{label}\"}}]}}";

    private static string IntegrityOnly(string route) =>
        $"{{\"Route\":\"{route}\",\"AssetFile\":\"{route}\",\"Selectors\":[],\"ResponseHeaders\":[],\"EndpointProperties\":[{{\"Name\":\"integrity\",\"Value\":\"sha256-x\"}}]}}";
}
