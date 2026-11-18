using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
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

namespace Trainer.Web.Release;

/// <summary>The three inputs of the static smoke suite, read from the environment.</summary>
/// <param name="Directory">The exported site (an absolute path).</param>
/// <param name="SiteUrl">The public site URL the export was made for; only its path prefix is served.</param>
/// <param name="PreviousPage">The live Practice page of the previous build, or null on the first deployment.</param>
public sealed record StaticSiteInputs(string Directory, Uri SiteUrl, string? PreviousPage);

/// <summary>One request that reached the served directory, with the body bytes the host sent for it.</summary>
public sealed record ServedRequest(string Path, int Status, long Bytes);

/// <summary>
/// The exported static site (a Release build exported and verified by <c>Trainer.StaticSite</c>) served by the Pages
/// emulator under its path prefix, plus one headless Chromium. <c>DIT_STATIC_SITE_DIR</c> names the export and
/// <c>DIT_STATIC_SITE_URL</c> the public URL it was made for; <c>DIT_PREVIOUS_PAGE</c> is the live Practice page of the previous
/// build, served at a test-only path so that page runs against this artifact's retained files. Without the first two every test
/// skips, unless <c>DIT_STATIC_SMOKE_REQUIRED=1</c>, which makes the absence a failure (CI sets it). A thin metering proxy sits
/// in front of the emulator: the service worker's own requests are invisible to the page's DevTools session, and only the host
/// sees every byte that was sent.
/// </summary>
public sealed class StaticSiteFolder : IAsyncLifetime
{
    public const string DirectoryVariable = "DIT_STATIC_SITE_DIR";
    public const string UrlVariable = "DIT_STATIC_SITE_URL";
    public const string PreviousPageVariable = "DIT_PREVIOUS_PAGE";
    public const string RequiredVariable = "DIT_STATIC_SMOKE_REQUIRED";

    private readonly ConcurrentDictionary<IPage, PageRecord> _pages = new();
    private readonly Dictionary<string, TimeSpan> _delays = new(StringComparer.Ordinal);
    private PagesEmulator? _emulator;
    private MeteringProxy? _proxy;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public StaticSiteFolder()
        : this(Environment.GetEnvironmentVariable)
    {
    }

    /// <summary>For tests of the input rules: reads the variables through <paramref name="variable"/>.</summary>
    /// <exception cref="InvalidOperationException">An input is missing or malformed while <c>DIT_STATIC_SMOKE_REQUIRED=1</c>.</exception>
    internal StaticSiteFolder(Func<string, string?> variable) => Inputs = Read(variable);

    public StaticSiteInputs? Inputs { get; }

    public bool IsAvailable => Inputs is not null;

    /// <summary>The prefix the site is served under, with both slashes: <c>/dit/</c>, or <c>/</c> for a root site.</summary>
    public string Prefix { get; private set; } = "/";

    /// <summary>The site's address on the loopback port, with its trailing slash.</summary>
    public Uri BaseAddress { get; private set; } = null!;

    /// <summary>Where the previous build's Practice page is served, or null when there is none.</summary>
    public Uri? PreviousPageAddress { get; private set; }

    public IBrowser Browser => _browser ?? throw new InvalidOperationException("The fixture is not started.");

    public IReadOnlyList<ServedRequest> Served => _proxy?.Requests ?? [];

    public static StaticSiteInputs? Read(Func<string, string?> variable)
    {
        ArgumentNullException.ThrowIfNull(variable);
        bool required = string.Equals(variable(RequiredVariable), "1", StringComparison.Ordinal);
        string? directory = variable(DirectoryVariable);
        string? url = variable(UrlVariable);
        string? missing = string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)
            ? $"{DirectoryVariable} (an existing directory)"
            : string.IsNullOrWhiteSpace(url) ? UrlVariable : null;
        if (missing is not null)
        {
            return required
                ? throw new InvalidOperationException($"{RequiredVariable}=1 but {missing} is not set: the static smoke suite has nothing to run against.")
                : null;
        }

        if (!Path.IsPathFullyQualified(directory!))
        {
            throw new InvalidOperationException($"{DirectoryVariable} must be an absolute path.");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? siteUrl) || (siteUrl.Scheme != Uri.UriSchemeHttp && siteUrl.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"{UrlVariable} must be an absolute http(s) URL.");
        }

        // Unset is the first deployment, which has no previous page. A value that names no file is a broken capture, and
        // skipping on it would make that look like a first deployment, so it fails whatever DIT_STATIC_SMOKE_REQUIRED says.
        string? previous = variable(PreviousPageVariable);
        if (!string.IsNullOrWhiteSpace(previous) && !File.Exists(previous))
        {
            throw new InvalidOperationException($"{PreviousPageVariable} is set to '{previous}', which is not a file: the previous deployment's page was not captured.");
        }

        return new StaticSiteInputs(
            Path.GetFullPath(directory!),
            siteUrl,
            string.IsNullOrWhiteSpace(previous) ? null : Path.GetFullPath(previous));
    }

    /// <summary>Skips the calling test when the export is not configured.</summary>
    public void SkipUnlessAvailable() => Assert.SkipUnless(
        IsAvailable,
        $"Set {DirectoryVariable} (an exported site) and {UrlVariable} (its public URL) to run the static smoke suite.");

    public async ValueTask InitializeAsync()
    {
        if (Inputs is null)
        {
            return;
        }

        try
        {
            // Brotli runtime: the loader fetches .br files, so the host compresses nothing, which is also the largest transfer.
            _emulator = await PagesEmulator.StartAsync(new PagesEmulatorOptions(Inputs.Directory, Inputs.SiteUrl, Gzip: false));
            Prefix = _emulator.BaseAddress.AbsolutePath;
            if (Inputs.PreviousPage is not null)
            {
                _emulator.MapExtra(Prefix + "__previous/en/dotnet/practice/", Inputs.PreviousPage);
            }

            _proxy = await MeteringProxy.StartAsync(new Uri(_emulator.BaseAddress.GetLeftPart(UriPartial.Authority)));
            BaseAddress = new Uri($"http://127.0.0.1:{_proxy.Port.ToString(CultureInfo.InvariantCulture)}{Prefix}");
            PreviousPageAddress = Inputs.PreviousPage is null ? null : new Uri(BaseAddress, "__previous/en/dotnet/practice/");
            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }
        catch
        {
            // xUnit does not call DisposeAsync when InitializeAsync throws, so the server and the browser would leak.
            await DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.CloseAsync();
        }

        _playwright?.Dispose();
        if (_proxy is not null)
        {
            await _proxy.DisposeAsync();
        }

        if (_emulator is not null)
        {
            await _emulator.DisposeAsync();
        }
    }

    public Uri Url(string relative) => new(BaseAddress, relative);

    /// <summary>Holds every response below the prefix whose path starts with <paramref name="relativePrefix"/> for <paramref name="delay"/>.</summary>
    public void Delay(string relativePrefix, TimeSpan delay)
    {
        _emulator!.Delay(Prefix + relativePrefix, delay);
        _delays[Prefix + relativePrefix] = delay;
    }

    /// <summary>Removes every delay and forgets the requests metered so far.</summary>
    public void Reset()
    {
        foreach (string prefix in _delays.Keys)
        {
            _emulator!.Delay(prefix, TimeSpan.Zero);
        }

        _delays.Clear();
        _proxy?.Clear();
    }

    /// <param name="seededOutbox">The answer log (<c>dit.outbox.v1.anon</c>) the context starts with.</param>
    /// <param name="seededCheckpoint">The progress checkpoint (<c>dit.progress.v1.anon</c>) it starts with: a visitor who comes back with a cleared HTTP cache still has it.</param>
    public async Task<IBrowserContext> NewContextAsync(bool mobile = false, string locale = "en-US", string? seededOutbox = null, string? seededCheckpoint = null)
    {
        IBrowserContext context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = BaseAddress.ToString(),
            Locale = locale,
            ViewportSize = mobile ? new ViewportSize { Width = MobileProfile.Width, Height = MobileProfile.Height } : new ViewportSize { Width = 1280, Height = 900 },
            HasTouch = mobile,
        });
        if (seededOutbox is not null)
        {
            // Once per context: a later document keeps whatever the island wrote since.
            await context.AddInitScriptAsync(
                $"try {{ if (localStorage.getItem('dit.outbox.v1.anon') === null) localStorage.setItem('dit.outbox.v1.anon', {System.Text.Json.JsonSerializer.Serialize(seededOutbox)}); }} catch (e) {{ }}");
        }

        if (seededCheckpoint is not null)
        {
            await context.AddInitScriptAsync(
                $"try {{ if (localStorage.getItem('dit.progress.v1.anon') === null) localStorage.setItem('dit.progress.v1.anon', {System.Text.Json.JsonSerializer.Serialize(seededCheckpoint)}); }} catch (e) {{ }}");
        }

        return context;
    }

    /// <summary>A tab that records every CSP violation and every console error of every document it shows.</summary>
    public async Task<IPage> NewPageAsync(IBrowserContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IPage tab = await context.NewPageAsync();
        PageRecord record = new();
        _pages[tab] = record;
        tab.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                record.ConsoleErrors.Enqueue(message.Text);
            }
        };
        tab.PageError += (_, error) => record.ConsoleErrors.Enqueue("uncaught: " + error);
        await tab.ExposeFunctionAsync("__ditReportCspViolation", (string violation) => record.Violations.Enqueue(violation));
        await tab.AddInitScriptAsync(
            """
            document.addEventListener('securitypolicyviolation', function (e) {
              window.__ditReportCspViolation(location.pathname + ': ' + e.violatedDirective + ' blocked ' + (e.blockedURI || 'inline') + ' at ' + e.sourceFile + ':' + e.lineNumber);
            });
            """);
        return tab;
    }

    public void AssertNoCspViolations(IPage page)
    {
        string[] violations = [.. Record(page).Violations];
        Assert.True(violations.Length == 0, "CSP violations: " + string.Join(" | ", violations));
    }

    /// <summary>Console errors and uncaught exceptions of the tab; <paramref name="ignore"/> drops the ones a test expects.</summary>
    public string[] ConsoleErrors(IPage page, Func<string, bool>? ignore = null) =>
        [.. Record(page).ConsoleErrors.Where(text => ignore is null || !ignore(text))];

    private PageRecord Record(IPage page) =>
        _pages.TryGetValue(page, out PageRecord? record) ? record : throw new InvalidOperationException("The page was not created with NewPageAsync.");

    private sealed class PageRecord
    {
        public ConcurrentQueue<string> Violations { get; } = new();

        public ConcurrentQueue<string> ConsoleErrors { get; } = new();
    }

    private sealed class MeteringProxy : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _client;
        private readonly Uri _origin;
        private readonly ConcurrentQueue<ServedRequest> _requests = new();

        private MeteringProxy(WebApplication app, HttpClient client, Uri origin, int port)
        {
            _app = app;
            _client = client;
            _origin = origin;
            Port = port;
        }

        public int Port { get; }

        public IReadOnlyList<ServedRequest> Requests => [.. _requests];

        public static async Task<MeteringProxy> StartAsync(Uri origin)
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
            MeteringProxy[] holder = new MeteringProxy[1];
            app.Run(context => holder[0].ForwardAsync(context));
            await app.StartAsync();
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            MeteringProxy proxy = new(app, client, origin, new Uri(address).Port);
            holder[0] = proxy;
            return proxy;
        }

        public void Clear()
        {
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

            _requests.Enqueue(new ServedRequest(context.Request.Path.Value!, (int)response.StatusCode, body.Length));
            if (!HttpMethods.IsHead(context.Request.Method))
            {
                await context.Response.Body.WriteAsync(body, context.RequestAborted);
            }
        }
    }
}
