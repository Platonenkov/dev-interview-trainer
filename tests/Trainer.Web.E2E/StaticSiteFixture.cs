using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Trainer.StaticSite;
using Trainer.TestSupport;

namespace Trainer.Web.E2E;

/// <summary>One request that reached the origin (the emulator), as the counting proxy in front of it saw it.</summary>
public sealed record OriginRequest(string PathAndQuery, int Status);

/// <summary>One exported build of the site: its directory, the app that was crawled and the content it was built from.</summary>
public sealed class StaticBuild(string buildId, string directory, TrainerWebFactory factory, ContentWorkspace? ownedContent)
{
    public string BuildId { get; } = buildId;

    public string Directory { get; } = directory;

    public TrainerWebFactory Factory { get; } = factory;

    internal ContentWorkspace? OwnedContent { get; } = ownedContent;

    public string FullPath(string relative) => Path.Combine(Directory, relative.Replace('/', Path.DirectorySeparatorChar));
}

/// <summary>
/// The exported static site (the real static-edition app, crawled in process and verified) served by the
/// <see cref="PagesEmulator"/> under <c>/dev-interview-trainer/</c> on a loopback port, as GitHub Pages would serve it.
/// A thin counting proxy sits in front of the emulator: a browser cache or a service worker answers some requests itself,
/// and only the proxy shows which ones reached the origin. One export is shared by a test class; a test that changes what is
/// served starts with <see cref="ResetAsync"/>.
/// </summary>
public sealed class StaticSiteFixture(PostgresFixture postgres, BrowserFixture browser) : IAsyncLifetime
{
    public const string Prefix = "/dev-interview-trainer/";

    /// <summary>The GoatCounter site code the export is configured with; the page-view beacon would go to its host.</summary>
    public const string GoatCounterCode = "e2e-static";

    private const string FirstBuildId = "e2ea0001.1.1";
    private static readonly string[] BuildIdPrefixes = ["e2eb", "e2ec", "e2ed", "e2ee", "e2ef"];

    private readonly List<StaticBuild> _builds = [];
    private readonly List<string> _scratchDirectories = [];
    private readonly Dictionary<string, TimeSpan> _delays = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _exportGate = new(1, 1);
    private PagesEmulator _emulator = null!;
    private OriginProxy _proxy = null!;
    private Lazy<Task<StaticBuild>>? _defaultSecondBuild;
    private int _buildCounter;
    private string _servedDirectory = string.Empty;
    private string? _workingCopy;

    public IBrowser Browser => browser.Browser;

    /// <summary>The site URL with its trailing slash: <c>http://127.0.0.1:{port}/dev-interview-trainer/</c>.</summary>
    public Uri BaseAddress { get; private set; } = null!;

    /// <summary>The first build (A), exported at startup from the web fixture content.</summary>
    public StaticBuild FirstBuild { get; private set; } = null!;

    public string Directory => FirstBuild.Directory;

    /// <summary>The services of the app the first build was crawled from, for comparing the browser's answers with the server's.</summary>
    public IServiceProvider Services => FirstBuild.Factory.Services;

    /// <summary>The directory the emulator serves right now.</summary>
    public string ServedDirectory => _servedDirectory;

    /// <summary>Every request that reached the origin since the last <see cref="ResetAsync"/>, in arrival order.</summary>
    public IReadOnlyList<OriginRequest> OriginRequests => _proxy.Requests;

    public async ValueTask InitializeAsync()
    {
        try
        {
            // The proxy needs the emulator's address and the export needs the proxy's port (the pages say their public URL),
            // so the emulator starts first over an empty directory and gets the first build once the port is known.
            string placeholder = NewScratchDirectory();
            _emulator = await PagesEmulator.StartAsync(new PagesEmulatorOptions(placeholder, new Uri("http://127.0.0.1" + Prefix)));
            _proxy = await OriginProxy.StartAsync(new Uri(_emulator.BaseAddress.GetLeftPart(UriPartial.Authority)));
            BaseAddress = new Uri($"http://127.0.0.1:{_proxy.Port.ToString(CultureInfo.InvariantCulture)}{Prefix}");

            FirstBuild = await ExportAsync(FirstBuildId, content: null, changeCode: false, retainFromServed: false);
            await SwapToAsync(FirstBuild.Directory);
            await ResetAsync();
        }
        catch
        {
            // xUnit does not call DisposeAsync when InitializeAsync throws, so the hosts would leak.
            await DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_defaultSecondBuild is { IsValueCreated: true, Value: { IsCompletedSuccessfully: false } pending })
        {
            try
            {
                await pending;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The test that asked for the build reports its failure; the cleanup below covers what it left.
            }
        }

        if (_proxy is not null)
        {
            await _proxy.DisposeAsync();
        }

        if (_emulator is not null)
        {
            await _emulator.DisposeAsync();
        }

        foreach (StaticBuild build in _builds)
        {
            await build.Factory.DisposeAsync();
            build.OwnedContent?.Dispose();
            DeleteDirectory(build.Directory);
        }

        foreach (string directory in _scratchDirectories)
        {
            DeleteDirectory(directory);
        }

        _exportGate.Dispose();
    }

    /// <summary>
    /// Another build with different content (a reworded question, so its shard name, the index and the sitemap differ) and the
    /// previous deployment's immutable files retained, as the real pipeline does. <paramref name="changeCode"/> also gives it
    /// its own stylesheet fingerprint and a changed worker script, which a new Release build would. The fixture takes ownership of
    /// <paramref name="content"/> and deletes it with the build.
    /// </summary>
    public async Task<StaticBuild> ExportBuildAsync(ContentWorkspace? content = null, bool changeCode = false)
    {
        string buildId = BuildIdPrefixes[_buildCounter++ % BuildIdPrefixes.Length] + (1000 + _buildCounter).ToString(CultureInfo.InvariantCulture) + ".2.1";
        return await ExportAsync(buildId, content, changeCode, retainFromServed: true);
    }

    /// <summary>The build most tests use as B: the default changed content and changed code, exported once per class.</summary>
    public Task<StaticBuild> GetSecondBuildAsync()
    {
        Lazy<Task<StaticBuild>> lazy = LazyInitializer.EnsureInitialized(
            ref _defaultSecondBuild,
            () => new Lazy<Task<StaticBuild>>(() => ExportBuildAsync(content: null, changeCode: true)));
        return lazy.Value;
    }

    /// <summary>Serves another directory from the next request on, as a new deployment replaces the site.</summary>
    public Task SwapToAsync(string directory)
    {
        _emulator.SwapRoot(directory);
        _servedDirectory = Path.GetFullPath(directory);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Deletes a file from the served directory (a boot dependency that is gone after the retention window).
    /// </summary>
    /// <param name="relativePath">Relative to the site root, for example <c>js/islands-start.abcdefghij.js</c>.</param>
    public async Task RemoveFromServedAsync(string relativePath)
    {
        string path = await WritablePathAsync(relativePath);
        Assert.True(File.Exists(path), $"{relativePath} is not in the served directory.");
        File.Delete(path);
    }

    /// <summary>Adds or replaces a file of the served directory.</summary>
    public async Task WriteToServedAsync(string relativePath, byte[] content)
    {
        string path = await WritablePathAsync(relativePath);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content, TestContext.Current.CancellationToken);
    }

    /// <summary>Rewrites the text of a file of the served directory.</summary>
    public async Task ReplaceInServedAsync(string relativePath, Func<string, string> change)
    {
        string path = await WritablePathAsync(relativePath);
        Assert.True(File.Exists(path), $"{relativePath} is not in the served directory.");
        string text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path, change(text), new UTF8Encoding(false), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Answers 404 for these paths (relative to the site root) without asking the origin, until <see cref="Unblock"/> or
    /// <see cref="ResetAsync"/>. A page route would do the same but makes the browser stop using its HTTP cache.
    /// </summary>
    public void Block(IEnumerable<string> relativePaths) => _proxy.Block(relativePaths.Select(path => Prefix + path));

    public void Unblock() => _proxy.Unblock();

    /// <summary>Adds a response header to every response whose path (below the site prefix) starts with <paramref name="relativePathPrefix"/>.</summary>
    public void AddResponseHeader(string relativePathPrefix, string name, string value) =>
        _proxy.AddHeader(Prefix + relativePathPrefix, name, value);

    // A build's own directory is shared by the whole class and by the next deployment's retention, so a test that edits
    // what is served edits a private copy of it.
    private async Task<string> WritablePathAsync(string relativePath)
    {
        if (_workingCopy is null || !string.Equals(_servedDirectory, _workingCopy, StringComparison.Ordinal))
        {
            string copy = NewScratchDirectory();
            await Task.Run(() => CopyDirectory(_servedDirectory, copy), TestContext.Current.CancellationToken);
            _workingCopy = Path.GetFullPath(copy);
            await SwapToAsync(copy);
        }

        return Path.Combine(_servedDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>Serves the first build again and forgets delays, added headers, the origin log and any edited copy.</summary>
    public async Task ResetAsync()
    {
        await SwapToAsync(FirstBuild.Directory);
        foreach (string prefix in _delays.Keys)
        {
            _emulator.Delay(prefix, TimeSpan.Zero);
        }

        _delays.Clear();
        _proxy.Clear();
        if (_workingCopy is not null)
        {
            DeleteDirectory(_workingCopy);
            _scratchDirectories.Remove(_workingCopy);
            _workingCopy = null;
        }
    }

    public void FailOnce(string relativePath, int status) => _emulator.FailOnce(Prefix + relativePath, status);

    public void Delay(string relativePathPrefix, TimeSpan delay)
    {
        _emulator.Delay(Prefix + relativePathPrefix, delay);
        _delays[Prefix + relativePathPrefix] = delay;
    }

    /// <summary>The number of requests for <paramref name="relativePath"/> (query ignored) that reached the origin.</summary>
    public int OriginCount(string relativePath) =>
        _proxy.Requests.Count(request => string.Equals(request.PathAndQuery.Split('?')[0], Prefix + relativePath, StringComparison.Ordinal));

    /// <summary>
    /// Empties the browser's HTTP cache through the DevTools Protocol, standing in for a return after the Pages
    /// <c>max-age=600</c> has run out. The Cache Storage the service worker keeps is not touched.
    /// </summary>
    public static async Task ExpireHttpCacheAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        ICDPSession session = await page.Context.NewCDPSessionAsync(page);
        try
        {
            await session.SendAsync("Network.enable");
            await session.SendAsync("Network.clearBrowserCache");
        }
        finally
        {
            await session.DetachAsync();
        }
    }

    public async Task<IBrowserContext> NewContextAsync(
        string locale = "en-US", ServiceWorkerPolicy serviceWorkers = ServiceWorkerPolicy.Allow, bool mobile = false)
    {
        IBrowserContext context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = BaseAddress.ToString(),
            Locale = locale,
            ServiceWorkers = serviceWorkers,
            ViewportSize = mobile ? new ViewportSize { Width = 375, Height = 812 } : new ViewportSize { Width = 1280, Height = 900 },
            HasTouch = mobile,
            IsMobile = mobile,
        });
        await context.AddInitScriptAsync("try { localStorage.setItem('dit.theme', 'dark'); } catch (e) { }");
        return context;
    }

    private readonly ConcurrentDictionary<IPage, PageWatch> _watches = new();

    /// <summary>A tab that records every request and every CSP violation of every document it shows.</summary>
    public async Task<IPage> NewPageAsync(IBrowserContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IPage tab = await context.NewPageAsync();
        _watches[tab] = await PageWatch.AttachAsync(tab);
        return tab;
    }

    /// <summary>The URL of every request the tab made, in order.</summary>
    public IReadOnlyList<string> RequestUrls(IPage page) => Watch(page).RequestUrls;

    public void AssertNoCspViolations(IPage page) => Watch(page).AssertNoCspViolations();

    /// <summary>
    /// Fails when the tab asked for anything outside the site prefix that is not a GoatCounter request: the static edition
    /// talks to no other host and no root-relative path may escape the prefix. <paramref name="baseAddress"/> names another
    /// emulator the tab was pointed at; it defaults to this fixture's.
    /// </summary>
    public void AssertRequestsStayUnderPrefix(IPage page, Uri? baseAddress = null)
    {
        Uri site = baseAddress ?? BaseAddress;
        string[] strays =
        [
            .. RequestUrls(page).Where(url =>
            {
                Uri uri = new(url);
                bool underPrefix = string.Equals(uri.GetLeftPart(UriPartial.Authority), site.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
                    && uri.AbsolutePath.StartsWith(Prefix, StringComparison.Ordinal);
                bool goatCounter = uri.Host.EndsWith(".goatcounter.com", StringComparison.OrdinalIgnoreCase);
                return !underPrefix && !goatCounter;
            }),
        ];
        Assert.True(strays.Length == 0, "Requests outside the prefix: " + string.Join(", ", strays));
    }

    /// <summary>Both end-of-test checks every test of the static site makes.</summary>
    public void AssertClean(IPage page)
    {
        AssertRequestsStayUnderPrefix(page);
        AssertNoCspViolations(page);
    }

    public Uri Url(string relative) => new(BaseAddress, relative);

    private PageWatch Watch(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return _watches.TryGetValue(page, out PageWatch? watch)
            ? watch
            : throw new InvalidOperationException("The page was not created with NewPageAsync.");
    }

    private async Task<StaticBuild> ExportAsync(string buildId, ContentWorkspace? content, bool changeCode, bool retainFromServed)
    {
        await _exportGate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            ContentWorkspace? owned = content is null && _builds.Count > 0 ? ChangedContent() : null;
            TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(
                postgres,
                new TrainerWebFactoryOptions
                {
                    Content = content ?? owned,
                    Settings = new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["App:Edition"] = "Static",
                        ["App:PublicBaseUrl"] = BaseAddress.ToString(),
                        ["App:StaticSite:BuildId"] = buildId,
                        // The StaticExport environment (where Pages sets this) demands an https public URL; the loopback site is http.
                        ["App:StaticSite:BrotliRuntime"] = "true",
                        ["App:Analytics:GoatCounterCode"] = GoatCounterCode,
                    },
                });
            string directory = NewScratchDirectory();
            DeleteDirectory(directory);
            StaticBuild build = new(buildId, directory, factory, owned ?? content);
            _builds.Add(build);
            await factory.WaitUntilReadyAsync();

            string pathBase = Uri.UnescapeDataString(BaseAddress.AbsolutePath).TrimEnd('/');
            SiteExporter exporter = new(factory.Server.CreateHandler(), TimeProvider.System, (span, ct) => Task.Delay(span, ct));
            await exporter.ExportAsync(
                new ExportOptions(new Uri("https://localhost" + pathBase), BaseAddress, directory, buildId, "0123456789abcdef0123456789abcdef01234567"),
                TestContext.Current.CancellationToken);

            if (changeCode)
            {
                ChangeCode(directory, buildId);
            }

            AddBrotliRuntime(directory);

            if (retainFromServed)
            {
                // The live site is what the emulator serves right now: the same call the pipeline makes against Pages.
                string live = _servedDirectory;
                string liveBuild = JsonSerializer.Deserialize(
                    File.ReadAllBytes(Path.Combine(live, "build.json")), ExportJsonContext.Default.BuildInfo)!.BuildId;
                Retention retention = new(
                    new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false },
                    TimeProvider.System,
                    (span, ct) => Task.Delay(span, ct));
                await retention.ApplyAsync(new RetentionOptions(BaseAddress, liveBuild, null), directory, TestContext.Current.CancellationToken);
            }

            IReadOnlyList<string> problems = ExportVerifier.Verify(directory, BaseAddress);
            if (!changeCode)
            {
                Assert.True(problems.Count == 0, "The export does not verify: " + string.Join(Environment.NewLine, problems.Take(20)));
            }

            return build;
        }
        finally
        {
            _exportGate.Release();
        }
    }

    // A reworded title moves the question's shard, the topic index and the sitemap, so the shard names of the build differ.
    private static ContentWorkspace ChangedContent()
    {
        ContentWorkspace workspace = ContentWorkspace.CopyOfWeb();
        workspace.Edit(
            "dotnet/csharp/closure-for-loop.yaml",
            "title: What does the code print when lambdas capture a for loop variable?",
            "title: What does the code print when lambdas capture a for loop variable, and why?");
        return workspace;
    }

    // What a new Release build changes that is visible to a cached page: the stylesheet gets another fingerprint, referenced
    // from every page, and the worker script differs, so a new worker installs.
    private static void ChangeCode(string directory, string buildId)
    {
        string css = System.IO.Directory.EnumerateFiles(Path.Combine(directory, "css"), "site.*.css").Single(file => Regex.IsMatch(Path.GetFileName(file), @"^site\.[a-z0-9]{10}\.css$"));
        string oldName = Path.GetFileName(css);
        string newName = "site." + new string(buildId.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).Concat("0000000000").Take(10).ToArray()) + ".css";
        File.WriteAllText(Path.Combine(directory, "css", newName), File.ReadAllText(css) + "\n/* " + buildId + " */\n", new UTF8Encoding(false));
        File.Delete(css);
        foreach (string file in System.IO.Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".html", StringComparison.Ordinal) || f.EndsWith("immutable.json", StringComparison.Ordinal)))
        {
            string text = File.ReadAllText(file);
            if (text.Contains(oldName, StringComparison.Ordinal))
            {
                File.WriteAllText(file, text.Replace(oldName, newName, StringComparison.Ordinal), new UTF8Encoding(false));
            }
        }

        string worker = Path.Combine(directory, "sw.js");
        File.WriteAllText(worker, File.ReadAllText(worker) + "\n// " + buildId + "\n", new UTF8Encoding(false));
    }

    // A Release publish compresses every static asset next to itself, and the export copies the .br of the runtime files. The
    // fixture runs a Debug build, which compresses nothing, so the files are made here with the names publish gives them
    // (<fingerprinted name>.br) and listed as immutable like the export does when BrotliRuntime is on.
    private static void AddBrotliRuntime(string directory)
    {
        string[] runtime =
        [
            .. System.IO.Directory.EnumerateFiles(Path.Combine(directory, "_framework"), "*", SearchOption.AllDirectories)
                .Where(file => Regex.IsMatch(Path.GetFileName(file), @"\.[a-z0-9]{10}\.(wasm|pdb)$", RegexOptions.CultureInvariant)),
        ];
        Assert.NotEmpty(runtime);
        foreach (string file in runtime)
        {
            using FileStream target = File.Create(file + ".br");
            using BrotliStream brotli = new(target, CompressionLevel.Fastest);
            using FileStream source = File.OpenRead(file);
            source.CopyTo(brotli);
        }

        string path = Path.Combine(directory, "immutable.json");
        ImmutableInfo info = JsonSerializer.Deserialize(File.ReadAllBytes(path), ExportJsonContext.Default.ImmutableInfo)!;
        string[] files =
        [
            .. info.Files.Concat(runtime.Select(file => Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/') + ".br")).Order(StringComparer.Ordinal),
        ];
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(info with { Files = files }, ExportJsonContext.Default.ImmutableInfo));
    }

    private string NewScratchDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dit-static-e2e-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        _scratchDirectories.Add(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        if (System.IO.Directory.Exists(directory))
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        System.IO.Directory.CreateDirectory(target);
        foreach (string file in System.IO.Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (string directory in System.IO.Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }
}

/// <summary>
/// A forwarding proxy that remembers which requests arrived. It passes bodies and encodings through untouched, so the
/// emulator's gzip and its one-shot failures behave as they do without it.
/// </summary>
internal sealed class OriginProxy : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly HttpClient _client;
    private readonly Uri _origin;
    private readonly ConcurrentQueue<OriginRequest> _requests = new();
    private readonly ConcurrentDictionary<string, byte> _blocked = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<(string Name, string Value)>> _headers = new(StringComparer.Ordinal);

    private OriginProxy(WebApplication app, HttpClient client, Uri origin, int port)
    {
        _app = app;
        _client = client;
        _origin = origin;
        Port = port;
    }

    public int Port { get; }

    public IReadOnlyList<OriginRequest> Requests => [.. _requests];

    public static async Task<OriginProxy> StartAsync(Uri origin)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        WebApplication app = builder.Build();
        HttpClient client = new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            UseProxy = false,
        });
        OriginProxy[] holder = new OriginProxy[1];
        app.Run(context => holder[0].ForwardAsync(context));
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        OriginProxy proxy = new(app, client, origin, new Uri(address).Port);
        holder[0] = proxy;
        return proxy;
    }

    public void Block(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            _blocked[path] = 0;
        }
    }

    public void Unblock() => _blocked.Clear();

    public void AddHeader(string pathPrefix, string name, string value) =>
        _headers.AddOrUpdate(pathPrefix, _ => [(name, value)], (_, list) => [.. list, (name, value)]);

    public void Clear()
    {
        _headers.Clear();
        _blocked.Clear();
        while (_requests.TryDequeue(out _))
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _client.Dispose();
    }

    private async Task ForwardAsync(HttpContext context)
    {
        string pathAndQuery = context.Request.Path.Value + context.Request.QueryString.Value;
        if (_blocked.ContainsKey(context.Request.Path.Value!))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            _requests.Enqueue(new OriginRequest(pathAndQuery, StatusCodes.Status404NotFound));
            return;
        }

        using HttpRequestMessage request = new(new HttpMethod(context.Request.Method), new Uri(_origin, pathAndQuery));
        if (context.Request.Headers.AcceptEncoding.Count > 0)
        {
            request.Headers.TryAddWithoutValidation("Accept-Encoding", context.Request.Headers.AcceptEncoding.ToArray());
        }

        using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, context.RequestAborted);
        byte[] body = await response.Content.ReadAsByteArrayAsync(context.RequestAborted);
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers.Concat(response.Content.Headers))
        {
            if (!string.Equals(header.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
        }

        foreach (KeyValuePair<string, List<(string Name, string Value)>> rule in _headers)
        {
            if (context.Request.Path.Value!.StartsWith(rule.Key, StringComparison.Ordinal))
            {
                foreach ((string name, string value) in rule.Value)
                {
                    context.Response.Headers[name] = value;
                }
            }
        }

        _requests.Enqueue(new OriginRequest(pathAndQuery, (int)response.StatusCode));
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.Body.WriteAsync(body, context.RequestAborted);
        }
    }
}
