using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Trainer.Web.StaticSite;

namespace Trainer.Web.E2E;

/// <summary>
/// The content-addressed service worker (ADR 0026) in a real browser: scope, claim, what it caches and what it never
/// does, and how a page recovers from one failed answer. The origin counts come from the proxy in front of the emulator, the
/// only place that shows which requests the worker or the HTTP cache answered by themselves.
/// </summary>
[Trait("Category", "E2E")]
public sealed class StaticServiceWorkerTests(StaticSiteFixture site) : IClassFixture<StaticSiteFixture>, IAsyncLifetime
{
    private const string CacheName = "dit-immutable-v1:" + StaticSiteFixture.Prefix;

    private static readonly Regex Eligible = new(StaticAssetRules.JavaScriptRegex, RegexOptions.CultureInvariant);

    private static float Wait => StaticSiteSteps.IslandTimeoutMs;

    public ValueTask InitializeAsync() => new(site.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<(IBrowserContext Context, IPage Tab)> OpenControlledAsync(string relativeUrl = "en/about/")
    {
        IBrowserContext context = await site.NewContextAsync();
        IPage tab = await site.NewPageAsync(context);
        await tab.GotoAsync(site.Url(relativeUrl).ToString());
        await StaticSiteSteps.WaitForServiceWorkerControlAsync(tab);
        return (context, tab);
    }

    // The island page, ready, controlled by the worker, and with every eligible file it loaded already in Cache Storage.
    private async Task<(IBrowserContext Context, IPage Tab)> PrimeIslandAsync(string relativeUrl = "en/dotnet/practice/")
    {
        IBrowserContext context = await site.NewContextAsync();
        IPage tab = await site.NewPageAsync(context);
        await tab.GotoAsync(site.Url(relativeUrl).ToString());
        await StaticSiteSteps.WaitForIslandAsync(tab);
        await StaticSiteSteps.WaitForServiceWorkerControlAsync(tab);
        await WaitForFillAsync(tab);
        return (context, tab);
    }

    private static async Task<string[]> NotYetCachedAsync(IPage tab) =>
        await tab.EvaluateAsync<string[]>(
            """
            async ([name, pattern, scope]) => {
              const re = new RegExp(pattern);
              const cache = await caches.open(name);
              const stored = new Set((await cache.keys()).map(request => request.url));
              return [...new Set(performance.getEntriesByType('resource').map(entry => entry.name))].filter(text => {
                const url = new URL(text);
                return url.origin === location.origin && url.search === '' && url.pathname.startsWith(scope)
                  && re.test(url.pathname.slice(scope.length)) && !stored.has(url.href);
              });
            }
            """,
            new object[] { CacheName, StaticAssetRules.JavaScriptRegex, StaticSiteFixture.Prefix });

    private static async Task WaitForFillAsync(IPage tab)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        string[] missing;
        while ((missing = await NotYetCachedAsync(tab)).Length > 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "Not cached after 90 s: " + string.Join(", ", missing.Take(10)));
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }
    }

    private static async Task<string[]> CachedUrlsAsync(IPage tab) =>
        await tab.EvaluateAsync<string[]>(
            """
            async name => {
              const cache = await caches.open(name);
              return (await cache.keys()).map(request => request.url).filter(url => !url.endsWith('__recency'));
            }
            """,
            CacheName);

    private static string RelativeOf(string url) => new Uri(url).AbsolutePath[StaticSiteFixture.Prefix.Length..];

    private string[] OriginPathsSince(int before) =>
        [.. site.OriginRequests.Skip(before).Select(request => request.PathAndQuery.Split('?')[0][StaticSiteFixture.Prefix.Length..])];

    private string IndexPath()
    {
        string file = Directory.EnumerateFiles(Path.Combine(site.Directory, "data", "en", "dotnet"), "index.*.json").Single();
        return "data/en/dotnet/" + Path.GetFileName(file);
    }

    [Fact]
    public async Task ServiceWorker_ScopeIsTheSitePrefix()
    {
        (IBrowserContext context, IPage tab) = await OpenControlledAsync();
        await using IBrowserContext owned = context;

        string scope = await tab.EvaluateAsync<string>("async () => (await navigator.serviceWorker.ready).scope");

        Assert.EndsWith(StaticSiteFixture.Prefix, scope, StringComparison.Ordinal);
        Assert.Equal(site.BaseAddress.ToString(), scope);
        Assert.Equal(site.Url("sw.js").ToString(), await tab.EvaluateAsync<string>("async () => (await navigator.serviceWorker.ready).active.scriptURL"));
        site.AssertClean(tab);
    }

    [Fact]
    public async Task ServiceWorker_ClaimsTheFirstPageWithoutAReload()
    {
        await using IBrowserContext context = await site.NewContextAsync();
        IPage tab = await site.NewPageAsync(context);
        int navigations = 0;
        tab.FrameNavigated += (_, frame) =>
        {
            if (frame == tab.MainFrame)
            {
                Interlocked.Increment(ref navigations);
            }
        };

        await tab.GotoAsync(site.Url("en/about/").ToString());
        await StaticSiteSteps.WaitForServiceWorkerControlAsync(tab);

        // The page that registered the worker is controlled by it on activation: no second load was needed.
        Assert.Equal(1, navigations);
        Assert.Equal("activated", await tab.EvaluateAsync<string>("() => navigator.serviceWorker.controller.state"));
        site.AssertClean(tab);
    }

    [Fact]
    public async Task ServiceWorker_SecondVisitServesRuntimeFromCache()
    {
        (IBrowserContext context, IPage tab) = await PrimeIslandAsync();
        await using IBrowserContext owned = context;
        List<(string Url, bool FromWorker)> responses = [];
        tab.Response += (_, response) =>
        {
            lock (responses)
            {
                responses.Add((response.Url, response.FromServiceWorker));
            }
        };
        int originBefore = site.OriginRequests.Count;

        await tab.GotoAsync(site.Url("en/dotnet/practice/").ToString());
        await StaticSiteSteps.WaitForIslandAsync(tab);

        (string Url, bool FromWorker)[] runtime;
        lock (responses)
        {
            runtime = [.. responses.Where(r => RelativeOf(r.Url).StartsWith("_framework/", StringComparison.Ordinal) && Eligible.IsMatch(RelativeOf(r.Url)))];
        }

        Assert.True(runtime.Length > 100, $"only {runtime.Length} runtime responses were seen");
        Assert.All(runtime, r => Assert.True(r.FromWorker, r.Url + " did not come from the service worker"));
        Assert.DoesNotContain(OriginPathsSince(originBefore), path => path.StartsWith("_framework/", StringComparison.Ordinal) && Eligible.IsMatch(path));
        site.AssertClean(tab);
    }

    [Fact]
    public async Task ServiceWorker_ReturnAfterHttpCacheCleared_IsServedFromCache()
    {
        (IBrowserContext context, IPage tab) = await PrimeIslandAsync();
        await using IBrowserContext owned = context;
        await StaticSiteFixture.ExpireHttpCacheAsync(tab);
        int originBefore = site.OriginRequests.Count;

        await tab.GotoAsync(site.Url("en/dotnet/practice/").ToString());
        await StaticSiteSteps.WaitForIslandAsync(tab);

        string[] fetched = OriginPathsSince(originBefore);
        // The page itself is mutable and comes from the origin; every content-addressed file is answered by the worker.
        Assert.Contains("en/dotnet/practice/", fetched);
        Assert.DoesNotContain(fetched, path => Eligible.IsMatch(path));
        site.AssertClean(tab);
    }

    [Fact]
    public async Task ServiceWorker_VaryHeaderDoesNotDefeatTheCache()
    {
        // A cached copy is matched by name alone: the stylesheet request sends another Accept than the worker's fill.
        site.AddResponseHeader("css/", "Vary", "Accept");
        site.AddResponseHeader("js/", "Vary", "Accept");
        (IBrowserContext context, IPage tab) = await PrimeIslandAsync();
        await using IBrowserContext owned = context;
        List<(string Url, bool FromWorker)> responses = [];
        tab.Response += (_, response) =>
        {
            lock (responses)
            {
                responses.Add((response.Url, response.FromServiceWorker));
            }
        };
        await StaticSiteFixture.ExpireHttpCacheAsync(tab);
        int originBefore = site.OriginRequests.Count;

        await tab.GotoAsync(site.Url("en/dotnet/practice/").ToString());
        await StaticSiteSteps.WaitForIslandAsync(tab);

        (string Url, bool FromWorker)[] styles;
        lock (responses)
        {
            styles = [.. responses.Where(r => RelativeOf(r.Url).StartsWith("css/", StringComparison.Ordinal) && Eligible.IsMatch(RelativeOf(r.Url)))];
        }

        Assert.NotEmpty(styles);
        Assert.All(styles, r => Assert.True(r.FromWorker, r.Url + " was not answered by the worker"));
        Assert.DoesNotContain(OriginPathsSince(originBefore), path => (path.StartsWith("css/", StringComparison.Ordinal) || path.StartsWith("js/", StringComparison.Ordinal)) && Eligible.IsMatch(path));
        site.AssertClean(tab);
    }

    [Fact]
    public async Task ServiceWorker_ContentBrowsingDoesNotEvictRuntime()
    {
        const int Fillers = 650;
        for (int i = 0; i < Fillers; i++)
        {
            await site.WriteToServedAsync($"data/en/filler/{i.ToString("x16", CultureInfo.InvariantCulture)}.0123456789abcdef.json", Encoding.UTF8.GetBytes("{}"));
        }

        (IBrowserContext context, IPage tab) = await PrimeIslandAsync();
        await using IBrowserContext owned = context;
        string[] runtimeBefore = [.. (await CachedUrlsAsync(tab)).Where(url => !RelativeOf(url).StartsWith("data/", StringComparison.Ordinal))];
        Assert.True(runtimeBefore.Length > 100);

        await tab.EvaluateAsync(
            """
            async ([prefix, count]) => {
              for (let start = 0; start < count; start += 25) {
                await Promise.all(Array.from({ length: Math.min(25, count - start) }, (_, k) => {
                  const name = (start + k).toString(16).padStart(16, '0');
                  return fetch(prefix + 'data/en/filler/' + name + '.0123456789abcdef.json').then(r => r.text());
                }));
              }
            }
            """,
            new object[] { site.BaseAddress.ToString(), Fillers });

        // Stores and prunes finish after the responses; wait until the content count stops changing.
        int last = -1;
        int stable = 0;
        string[] now = [];
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (stable < 8)
        {
            Assert.True(DateTime.UtcNow < deadline, "the cache did not settle");
            await Task.Delay(250, TestContext.Current.CancellationToken);
            now = await CachedUrlsAsync(tab);
            int content = now.Count(url => RelativeOf(url).StartsWith("data/en/filler/", StringComparison.Ordinal));
            stable = content == last ? stable + 1 : 0;
            last = content;
        }

        string[] fillers = [.. now.Where(url => RelativeOf(url).StartsWith("data/en/filler/", StringComparison.Ordinal))];
        // Content is bounded by count (600) and a prune evicts down to 90%: the oldest content went, no runtime file did.
        Assert.InRange(fillers.Length, 400, 600);
        Assert.DoesNotContain(fillers, url => RelativeOf(url).Contains("/0000000000000000.", StringComparison.Ordinal));
        Assert.Empty(runtimeBefore.Except(now, StringComparer.Ordinal));
        site.AssertClean(tab);
    }

    [Fact]
    public async Task ServiceWorker_QuotaFailureStillServesTheResponse()
    {
        // cache.put is the one call a full disk breaks; it is replaced inside the worker, ahead of its own code.
        await site.ReplaceInServedAsync(
            "sw.js",
            text => "Cache.prototype.put = function () { return Promise.reject(new DOMException('full', 'QuotaExceededError')); };\n" + text);
        await using IBrowserContext context = await site.NewContextAsync();
        IPage tab = await site.NewPageAsync(context);
        await tab.GotoAsync(site.Url("en/dotnet/practice/").ToString());
        await StaticSiteSteps.WaitForIslandAsync(tab);
        await StaticSiteSteps.WaitForServiceWorkerControlAsync(tab);
        List<(string Url, bool FromWorker)> responses = [];
        tab.Response += (_, response) =>
        {
            lock (responses)
            {
                responses.Add((response.Url, response.FromServiceWorker));
            }
        };

        string index = IndexPath();
        string body = await tab.EvaluateAsync<string>("async url => { const r = await fetch(url); if (!r.ok) throw new Error(String(r.status)); return await r.text(); }", site.Url(index).ToString());

        Assert.Contains("\"format\"", body, StringComparison.OrdinalIgnoreCase);
        lock (responses)
        {
            Assert.Contains(responses, r => r.Url == site.Url(index).ToString() && r.FromWorker);
        }

        // Nothing was stored, and nothing broke: a navigation after the failure still boots the island.
        Assert.Empty(await CachedUrlsAsync(tab));
        await tab.ReloadAsync();
        await StaticSiteSteps.WaitForIslandAsync(tab);
        site.AssertClean(tab);
    }

    [Fact]
    public async Task ServiceWorker_NavigationWithoutPreload_FallsBackToFetch()
    {
        await site.ReplaceInServedAsync(
            "sw.js",
            text => "NavigationPreloadManager.prototype.enable = function () { return Promise.resolve(); };\n" + text);
        (IBrowserContext context, IPage tab) = await OpenControlledAsync();
        await using IBrowserContext owned = context;
        Assert.False(await tab.EvaluateAsync<bool>("async () => (await (await navigator.serviceWorker.ready).navigationPreload.getState()).enabled"));

        IResponse? response = await tab.GotoAsync(site.Url("en/dotnet/questions/").ToString());

        Assert.Equal(200, response!.Status);
        Assert.True(response.FromServiceWorker);
        Assert.Equal(1, OriginPathsSince(0).Count(path => path == "en/dotnet/questions/"));
        Assert.Equal(1, await tab.Locator("h1.catalog__heading").CountAsync());
        site.AssertClean(tab);
    }

    [Fact]
    public async Task ServiceWorker_NavigationDownloadedOnce()
    {
        (IBrowserContext context, IPage tab) = await OpenControlledAsync();
        await using IBrowserContext owned = context;
        Assert.True(await tab.EvaluateAsync<bool>("async () => (await (await navigator.serviceWorker.ready).navigationPreload.getState()).enabled"));

        foreach (string page in new[] { "en/dotnet/questions/", "en/dotnet/topics/async/", "ru/dotnet/questions/closure-for-loop/" })
        {
            IResponse? response = await tab.GotoAsync(site.Url(page).ToString());
            Assert.Equal(200, response!.Status);
            Assert.True(response.FromServiceWorker, page + " did not go through the worker");
            // The worker takes the preloaded response instead of fetching again.
            Assert.Equal(1, OriginPathsSince(0).Count(path => path == page));
        }

        site.AssertClean(tab);
    }

    [Fact]
    public async Task ServiceWorker_MutableAliasNeverCached()
    {
        (IBrowserContext context, IPage tab) = await PrimeIslandAsync();
        await using IBrowserContext owned = context;
        string[] aliases = ["_framework/blazor.web.js", "build.json", "immutable.json", "css/site.css", "js/outbox.js", "sw.js"];
        List<(string Url, bool FromWorker)> responses = [];
        tab.Response += (_, response) =>
        {
            lock (responses)
            {
                responses.Add((response.Url, response.FromServiceWorker));
            }
        };

        foreach (string alias in aliases)
        {
            Assert.False(Eligible.IsMatch(alias), alias + " matches the rule");
            int status = await tab.EvaluateAsync<int>("async url => (await fetch(url)).status", site.Url(alias).ToString());
            Assert.Equal(200, status);
        }

        string[] cached = [.. (await CachedUrlsAsync(tab)).Select(RelativeOf)];
        Assert.DoesNotContain(cached, path => aliases.Contains(path));
        Assert.All(cached, path => Assert.Matches(Eligible, path));
        lock (responses)
        {
            Assert.All(responses.Where(r => aliases.Contains(RelativeOf(r.Url))), r => Assert.False(r.FromWorker, r.Url + " was answered by the worker"));
        }

        site.AssertClean(tab);
    }

    [Fact]
    public async Task ServiceWorker_RedirectedAnswerIsNotStored()
    {
        // A name that matches the rule but is answered through a redirect (Pages redirects a directory to its slashed URL).
        await site.WriteToServedAsync("data/en/odd.0123456789abcdef.json/index.html", Encoding.UTF8.GetBytes("<html>not a shard</html>"));
        (IBrowserContext context, IPage tab) = await OpenControlledAsync();
        await using IBrowserContext owned = context;

        bool redirected = await tab.EvaluateAsync<bool>("async url => (await fetch(url)).redirected", site.Url("data/en/odd.0123456789abcdef.json").ToString());

        Assert.True(redirected);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(await CachedUrlsAsync(tab), url => url.Contains("odd.0123456789abcdef", StringComparison.Ordinal));
        site.AssertClean(tab);
    }

    [Fact]
    public async Task ServiceWorker_FirstVisitFillNeedsNoExtraNetwork()
    {
        (IBrowserContext context, IPage tab) = await PrimeIslandAsync();
        await using IBrowserContext owned = context;

        string[] eligible = [.. OriginPathsSince(0).Where(path => Eligible.IsMatch(path))];

        // The worker's fill asks for what the page already loaded: the HTTP cache answers it, and each file reached the origin once.
        Assert.True(eligible.Length > 100, $"only {eligible.Length} content-addressed requests reached the origin");
        string[] repeated = [.. eligible.GroupBy(path => path, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key)];
        Assert.True(repeated.Length == 0, "Requested more than once: " + string.Join(", ", repeated.Take(10)));
        Assert.Empty(await NotYetCachedAsync(tab));
        site.AssertClean(tab);
    }

    [Fact]
    public Task ServiceWorker_404Then200_SecondRequestReachesNetworkAndPageRecovers() => RecoverAfterOneFailureAsync(404);

    [Fact]
    public Task ServiceWorker_503Then200_SecondRequestReachesNetworkAndPageRecovers() => RecoverAfterOneFailureAsync(503);

    private async Task RecoverAfterOneFailureAsync(int status)
    {
        (IBrowserContext context, IPage tab) = await OpenControlledAsync();
        await using IBrowserContext owned = context;
        await tab.EvaluateAsync("() => localStorage.setItem('dit.kept', 'yes')");
        string index = IndexPath();
        site.FailOnce(index, status);

        // The island is usable without the index (its filters need none); the sidebar simply has no numbers yet.
        await tab.GotoAsync(site.Url("en/dotnet/practice/").ToString());
        await StaticSiteSteps.WaitForIslandAsync(tab);
        Assert.Equal(0, await tab.Locator(".side__known").CountAsync());
        Assert.Null(await CachedStatusAsync(tab, index));
        string[] cachesBefore = await tab.EvaluateAsync<string[]>("async () => await caches.keys()");

        // Start asks for the index again; the worker kept nothing of the failure, so the request reaches the network.
        await tab.Locator(".pf__start").ClickAsync();
        await tab.Locator(StaticSiteSteps.Question).WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });

        Assert.Equal(2, OriginPathsSince(0).Count(path => path == index));
        Assert.Equal(new[] { status, 200 }, site.OriginRequests.Where(request => request.PathAndQuery == StaticSiteFixture.Prefix + index).Select(request => request.Status));
        Assert.Equal(200, await CachedStatusAsync(tab, index));
        // Nothing was cleared to get there.
        Assert.Equal(cachesBefore, await tab.EvaluateAsync<string[]>("async () => await caches.keys()"));
        Assert.Equal("yes", await tab.EvaluateAsync<string>("() => localStorage.getItem('dit.kept')"));
        site.AssertClean(tab);
    }

    private async Task<int?> CachedStatusAsync(IPage tab, string relativePath) =>
        await tab.EvaluateAsync<int?>(
            """
            async ([name, url]) => {
              const cache = await caches.open(name);
              const hit = await cache.match(url, { ignoreVary: true });
              return hit ? hit.status : null;
            }
            """,
            new object[] { CacheName, site.Url(relativePath).ToString() });
}
