using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Trainer.TestSupport;
using Trainer.Web.Startup;

namespace Trainer.Web.Tests;

/// <summary>The inline recovery script, its hidden banner and the elements it watches (design 5.13).</summary>
public sealed class RecoveryTests(StaticCspSiteFixture site) : IClassFixture<StaticCspSiteFixture>
{
    private async Task<IHtmlDocument> DocumentAsync(string path)
    {
        (HttpResponseMessage response, _, IHtmlDocument document) = await StaticCspTests.FetchAsync(site.Factory, path);
        response.Dispose();
        return document;
    }

    [Fact]
    public async Task EveryStaticPage_HasTheRecoveryScriptAllowedByHashAndTheHiddenBanner()
    {
        foreach (string path in StaticCspTests.ExportedLikePages)
        {
            IHtmlDocument document = await DocumentAsync(path);

            IElement script = Assert.Single(document.Head!.QuerySelectorAll("script"), s => s.TextContent == RecoveryScript.Source);
            Assert.False(script.HasAttribute("src"));
            string csp = StaticCspTests.CspOf(document);
            Assert.Contains(RecoveryScript.CspSource, csp, StringComparison.Ordinal);
            Assert.Equal(StaticCsp.Sha256Source(script.TextContent), RecoveryScript.CspSource);

            IElement banner = Assert.IsAssignableFrom<IElement>(document.QuerySelector("#site-updated"));
            Assert.True(banner.HasAttribute("hidden"), path);
            Assert.Equal("alert", banner.GetAttribute("role"));
            Assert.Equal("site-updated", banner.GetAttribute("class"));
            Assert.NotNull(banner.QuerySelector("a[data-reload]"));
            Assert.Same(banner, document.Body!.FirstElementChild);
        }
    }

    [Theory]
    [InlineData("/dit/en/about/", "Part of the page did not load.", "Reload the page.")]
    [InlineData("/dit/ru/about/", "Часть страницы не загрузилась.", "Перезагрузить страницу.")]
    public async Task Banner_IsInThePagesLanguage(string path, string text, string reload)
    {
        IHtmlDocument document = await DocumentAsync(path);

        IElement banner = Assert.IsAssignableFrom<IElement>(document.QuerySelector("#site-updated"));
        Assert.Contains(text, banner.TextContent, StringComparison.Ordinal);
        Assert.Equal(reload, banner.QuerySelector("a[data-reload]")!.TextContent.Trim());
    }

    [Fact]
    public async Task RecoverMarks_OnBootCriticalElementsOnly_NotOnAnalytics()
    {
        IHtmlDocument island = await DocumentAsync("/dit/en/dotnet/practice/");

        string[] marked = [.. island.QuerySelectorAll("[data-recover]").Select(e => e.GetAttribute("href") ?? e.GetAttribute("src") ?? string.Empty)];
        Assert.Equal(3, marked.Count(m => m.Contains(".css", StringComparison.Ordinal)));
        Assert.Contains(marked, m => m.Contains("islands-start", StringComparison.Ordinal));
        Assert.Contains(marked, m => m.Contains("blazor.web", StringComparison.Ordinal));
        Assert.Equal(5, marked.Length);
        Assert.All(island.QuerySelectorAll("link[rel=\"stylesheet\"]"), l => Assert.True(l.HasAttribute("data-recover")));

        IHtmlDocument page = await DocumentAsync("/dit/en/dotnet/questions/");
        Assert.Equal(2, page.QuerySelectorAll("[data-recover]").Length);
        Assert.Empty(page.QuerySelectorAll("script[data-recover]"));

        foreach (IHtmlDocument document in new[] { island, page })
        {
            Assert.NotEmpty(document.QuerySelectorAll("script[data-goatcounter]"));
            Assert.All(
                document.QuerySelectorAll("script[data-goatcounter], script[src*=\"goatcounter-settings\"]"),
                s => Assert.False(s.HasAttribute("data-recover")));
        }
    }

    [Fact]
    public async Task Chooser_WatchesItsStylesheets()
    {
        IHtmlDocument chooser = await DocumentAsync("/dit/");

        Assert.All(chooser.QuerySelectorAll("link[rel=\"stylesheet\"]"), l => Assert.True(l.HasAttribute("data-recover")));
        Assert.Equal(2, chooser.QuerySelectorAll("link[data-recover]").Length);
    }

    [Fact]
    public void RecoveryScript_ListensInTheCapturePhaseAndExposesTheHook()
    {
        Assert.Contains("window.addEventListener('error',", RecoveryScript.Source, StringComparison.Ordinal);
        Assert.EndsWith("},true);})();", RecoveryScript.Source, StringComparison.Ordinal);
        Assert.Contains("hasAttribute('data-recover')", RecoveryScript.Source, StringComparison.Ordinal);
        Assert.Contains("window.ditRecover=", RecoveryScript.Source, StringComparison.Ordinal);
        Assert.Contains("q.delete('_r')", RecoveryScript.Source, StringComparison.Ordinal);
        Assert.Contains("'dit.recover.'+location.pathname+q.toString()", RecoveryScript.Source, StringComparison.Ordinal);
        Assert.Contains("p.set('_r',", RecoveryScript.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("location.pathname+'?'", RecoveryScript.Source, StringComparison.Ordinal);
        Assert.Contains("DOMContentLoaded", RecoveryScript.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("</script", RecoveryScript.Source, StringComparison.OrdinalIgnoreCase);
        Assert.All(RecoveryScript.Source, c => Assert.True(c < 128, "ASCII only, so the hash does not depend on an encoding"));
    }

    private const string Harness = """
        const vm = require('vm');
        const source = process.argv[2];
        // A load is one run of the script on one URL; a scenario may reload to other search strings in the same session, which
        // share the storage the way a page and its reloaded self do.
        function load(scenario, search, store) {
          const banner = { hidden: true };
          const link = { href: '' };
          banner.querySelector = (selector) => (selector === '[data-reload]' ? link : null);
          const listeners = {};
          const sandbox = {
            URL, URLSearchParams, String, Date,
            location: { pathname: scenario.pathname, search, href: 'https://example.test' + scenario.pathname + search },
            sessionStorage: scenario.blockedStorage
              ? { getItem() { throw new Error('blocked'); }, setItem() { throw new Error('blocked'); } }
              : { getItem: (k) => (store.has(k) ? store.get(k) : null), setItem: (k, v) => store.set(k, v) },
            document: {
              readyState: 'complete',
              getElementById: (id) => (id === 'site-updated' ? banner : null),
              addEventListener() {},
            },
          };
          sandbox.window = sandbox;
          sandbox.window.addEventListener = (type, handler, capture) => { listeners[type] = { handler, capture }; };
          vm.runInNewContext(source, sandbox);
          const results = [];
          for (const step of scenario.steps) {
            banner.hidden = true;
            if (step === 'hook') {
              sandbox.ditRecover();
            } else {
              const target = { hasAttribute: (name) => name === 'data-recover' && step === 'marked' };
              listeners.error.handler({ target: step === 'window' ? sandbox : target });
            }
            results.push({ hidden: banner.hidden, href: link.href });
          }
          return { capture: listeners.error.capture, results };
        }

        function run(scenario) {
          const store = new Map();
          const first = load(scenario, scenario.search, store);
          const reloads = (scenario.reloads || []).map((search) => load(scenario, search, store).results);
          return { capture: first.capture, results: first.results, reloads };
        }
        process.stdout.write(JSON.stringify(JSON.parse(process.argv[3]).map(run)));
        """;

    private static readonly string[] ReloadSearches = ["?q=a&_r=200", "?q=a&_r=300", "?q=a", "?q=b&_r=400"];

    private static string[] Steps(params string[] steps) => steps;

    private static async Task<JsonElement[]> RunAsync(object[] scenarios)
    {
        using JsonDocument result = await NodeHarness.RunAsync(
            Harness,
            TestContext.Current.CancellationToken,
            RecoveryScript.Source,
            JsonSerializer.Serialize(scenarios));
        return [.. result.RootElement.EnumerateArray().Select(e => e.Clone())];
    }

    [Fact]
    public async Task Script_ShowsTheBannerOncePerUrlAndBuildsASameSiteReloadLink()
    {
        JsonElement[] runs = await RunAsync(
        [
            new { pathname = "/dit/en/dotnet/questions/", search = "?q=a+b&_r=1&level=junior", steps = Steps("marked", "marked", "hook") },
            new { pathname = "/dit/en/", search = "", steps = Steps("unmarked", "window", "hook", "hook") },
            new { pathname = "/dit/en/", search = "", steps = Steps("hook", "hook"), blockedStorage = true },
        ]);

        Assert.True(runs[0].GetProperty("capture").GetBoolean(), "the error listener must run in the capture phase");
        JsonElement[] first = [.. runs[0].GetProperty("results").EnumerateArray()];
        Assert.False(first[0].GetProperty("hidden").GetBoolean());
        Uri link = new(first[0].GetProperty("href").GetString()!);
        Assert.Equal("https://example.test/dit/en/dotnet/questions/", link.GetLeftPart(UriPartial.Path));
        System.Collections.Specialized.NameValueCollection query = System.Web.HttpUtility.ParseQueryString(link.Query);
        Assert.Equal("a b", query["q"]);
        Assert.Equal("junior", query["level"]);
        Assert.NotEqual("1", query["_r"]);
        Assert.Single(query.GetValues("_r")!);
        Assert.True(first[1].GetProperty("hidden").GetBoolean(), "a second failure on the same URL must not show it again");
        Assert.True(first[2].GetProperty("hidden").GetBoolean());

        JsonElement[] second = [.. runs[1].GetProperty("results").EnumerateArray()];
        Assert.True(second[0].GetProperty("hidden").GetBoolean(), "an element without data-recover is ignored");
        Assert.True(second[1].GetProperty("hidden").GetBoolean(), "a script error on window is ignored");
        Assert.False(second[2].GetProperty("hidden").GetBoolean());
        Assert.True(second[3].GetProperty("hidden").GetBoolean());

        JsonElement[] blocked = [.. runs[2].GetProperty("results").EnumerateArray()];
        Assert.All(blocked, r => Assert.False(r.GetProperty("hidden").GetBoolean()));
    }

    [Fact]
    public async Task Script_ShowsTheBannerOncePerPageAndSession_EvenAfterItsOwnReloadLinkAddedAFreshReloadParameter()
    {
        JsonElement[] runs = await RunAsync(
        [
            // The reload link adds a new _r, so the reloaded URL differs from the first one only in that parameter.
            new { pathname = "/dit/en/dotnet/questions/", search = "?q=a&_r=100", steps = Steps("hook"), reloads = ReloadSearches },
        ]);

        Assert.False(runs[0].GetProperty("results")[0].GetProperty("hidden").GetBoolean());
        JsonElement[] reloads = [.. runs[0].GetProperty("reloads").EnumerateArray()];
        Assert.True(reloads[0][0].GetProperty("hidden").GetBoolean(), "the reloaded page must not show the banner again");
        Assert.True(reloads[1][0].GetProperty("hidden").GetBoolean());
        Assert.True(reloads[2][0].GetProperty("hidden").GetBoolean(), "the same page without a reload parameter is the same page");
        Assert.False(reloads[3][0].GetProperty("hidden").GetBoolean(), "another query is another page");
    }

    [Theory]
    [InlineData("//evil.example/x/")]
    [InlineData("//evil.example")]
    public async Task ReloadLink_NeverLeavesTheSite_EvenForAPathThatStartsWithTwoSlashes(string pathname)
    {
        JsonElement[] runs = await RunAsync([new { pathname, search = "?a=1", steps = Steps("hook") }]);

        string href = runs[0].GetProperty("results")[0].GetProperty("href").GetString()!;
        Uri link = new(href);
        Assert.Equal("example.test", link.Host);
        Assert.Equal("https", link.Scheme);
        Assert.StartsWith("?", new Uri(href).Query, StringComparison.Ordinal);
    }

    private const string PlatformFailureHarness = """
        const fs = require('fs');
        const vm = require('vm');
        const source = fs.readFileSync(process.argv[2], 'utf8');

        function run(scenario) {
          const listeners = {};
          let recovered = 0;
          const skeleton = { hidden: false };
          const bootError = { hidden: true };
          const sandbox = {
            console: { error() {}, warn() {}, log() {} },
            Promise, Object, String, Int8Array, Uint8Array, Response, URL, btoa,
            fetch: async () => new Response(''),
            crypto: {},
            // Blazor Web resolves start() before the WebAssembly runtime starts, so a failing runtime never rejects it.
            Blazor: { start() { return new Promise(() => {}); }, addEventListener() {} },
            performance: { now: () => 0 },
            location: { search: '' },
            matchMedia: () => ({ matches: false }),
            MutationObserver: class { observe() {} disconnect() {} },
            ditRecover() { recovered++; },
            document: {
              documentElement: { lang: 'en', dataset: { edition: scenario.edition } },
              baseURI: 'https://example.test/dit/',
              currentScript: { dataset: {} },
              querySelectorAll: (selector) => selector === '.island-skeleton' ? [skeleton] : selector === '.island-boot-error' ? [bootError] : [],
              querySelector: () => null,
            },
          };
          sandbox.window = sandbox;
          sandbox.addEventListener = (type, handler) => { listeners[type] = handler; };
          vm.runInNewContext(source, sandbox);
          const reason = scenario.reason === null ? undefined : (scenario.isError ? new Error(scenario.reason) : scenario.reason);
          if (listeners.unhandledrejection) listeners.unhandledrejection({ reason });
          return { registered: typeof listeners.unhandledrejection === 'function', recovered, skeletonHidden: skeleton.hidden, errorShown: bootError.hidden === false };
        }
        process.stdout.write(JSON.stringify(JSON.parse(process.argv[3]).map(run)));
        """;

    [Fact]
    public async Task IslandsStart_ReportsAFailedRuntimeStart_ThatBlazorStartDoesNotReject()
    {
        const string platform = "Failed to start platform. Reason: Error: download 'https://example.test/dit/_framework/x.wasm' failed 404 Not Found";
        using JsonDocument result = await NodeHarness.RunAsync(
            PlatformFailureHarness,
            TestContext.Current.CancellationToken,
            NodeHarness.WwwRootFile("js", "islands-start.js"),
            JsonSerializer.Serialize(new object[]
            {
                new { edition = "static", reason = platform, isError = true },
                new { edition = "static", reason = platform, isError = false },
                new { edition = "", reason = platform, isError = true },
                new { edition = "static", reason = "some other rejection", isError = true },
                new { edition = "static", reason = (string?)null, isError = false },
            }));
        JsonElement[] runs = [.. result.RootElement.EnumerateArray().Select(e => e.Clone())];

        Assert.All(runs, run => Assert.True(run.GetProperty("registered").GetBoolean(), "a rejection listener must be registered"));
        // The static edition shows the skeleton's replacement and the reload offer; the server edition only the first.
        Assert.True(runs[0].GetProperty("errorShown").GetBoolean());
        Assert.True(runs[0].GetProperty("skeletonHidden").GetBoolean());
        Assert.Equal(1, runs[0].GetProperty("recovered").GetInt32());
        Assert.Equal(1, runs[1].GetProperty("recovered").GetInt32());
        Assert.True(runs[2].GetProperty("errorShown").GetBoolean());
        Assert.Equal(0, runs[2].GetProperty("recovered").GetInt32());
        foreach (JsonElement unrelated in runs.Skip(3))
        {
            Assert.False(unrelated.GetProperty("errorShown").GetBoolean());
            Assert.False(unrelated.GetProperty("skeletonHidden").GetBoolean());
            Assert.Equal(0, unrelated.GetProperty("recovered").GetInt32());
        }
    }

    [Fact]
    public void IslandsStart_CallsDitRecoverOnBootFailureInTheStaticEdition()
    {
        string script = File.ReadAllText(Path.Combine(ContentWorkspace.RepositoryRoot(), "src", "Trainer.Web", "wwwroot", "js", "islands-start.js"));
        int failure = script.IndexOf("function showBootFailure", StringComparison.Ordinal);
        int started = script.IndexOf("function started", StringComparison.Ordinal);

        string body = script[failure..started];
        Assert.Contains("window.ditRecover()", body, StringComparison.Ordinal);
        Assert.Contains("dataset.edition === 'static'", body, StringComparison.Ordinal);
    }
}
