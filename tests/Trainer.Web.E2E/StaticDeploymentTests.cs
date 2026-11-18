using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Trainer.TestSupport;
using Trainer.Web.StaticSite;

namespace Trainer.Web.E2E;

/// <summary>
/// A deployment replaces the whole site while pages of the previous one are open or cached: the first build (A) is open,
/// the second (B) goes live with A's immutable files retained, and a cached or open page of A must keep working or say what
/// to do. Also what the page does when a file it needs is gone, which is the recovery banner of design 5.13. Every test
/// starts from A being live.
/// </summary>
[Trait("Category", "E2E")]
public sealed class StaticDeploymentTests(StaticSiteFixture site) : IClassFixture<StaticSiteFixture>, IAsyncLifetime
{
    private const string PracticePath = "en/dotnet/practice/";
    private const string Banner = "#site-updated";

    private static readonly Regex Eligible = new(StaticAssetRules.JavaScriptRegex, RegexOptions.CultureInvariant);

    private static float Wait => StaticSiteSteps.IslandTimeoutMs;

    public ValueTask InitializeAsync() => new(site.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static ILocator Reload(IPage tab) => tab.Locator(Banner + " a[data-reload]");

    private string[] OriginPathsSince(int before) =>
        [.. site.OriginRequests.Skip(before).Select(request => request.PathAndQuery.Split('?')[0][StaticSiteFixture.Prefix.Length..])];

    // A boot failure replaces the skeleton with the server edition's own error message and offers the reload.
    private static async Task AssertBootErrorShownAsync(IPage tab)
    {
        await tab.Locator(".island-boot-error").First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        Assert.Equal(0, await tab.Locator(".island-skeleton:visible").CountAsync());
    }

    private static async Task AssertBannerVisibleAsync(IPage tab) =>
        await tab.Locator(Banner).WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });

    // A boot dependency that is gone: the page of this build asks for it and cannot get it.
    private async Task OpenWithMissingAsync(IPage tab, string relativeUrl, params string[] missing)
    {
        foreach (string path in missing)
        {
            await site.RemoveFromServedAsync(path);
        }

        await tab.GotoAsync(site.Url(relativeUrl).ToString());
    }

    private async Task RunAtoBAsync(ServiceWorkerPolicy serviceWorkers)
    {
        await using IBrowserContext context = await site.NewContextAsync(serviceWorkers: serviceWorkers);
        IPage tab = await StaticSiteSteps.OpenIslandAsync(site, context, PracticePath);
        if (serviceWorkers == ServiceWorkerPolicy.Allow)
        {
            await StaticSiteSteps.WaitForServiceWorkerControlAsync(tab);
        }

        StaticBuild b = await site.GetSecondBuildAsync();
        await site.SwapToAsync(b.Directory);
        int mark = site.OriginRequests.Count;

        // A plain navigation takes the page from the HTTP cache (max-age=600): it is A's page, on a site that is now B.
        await tab.GotoAsync(site.Url(PracticePath).ToString());
        await StaticSiteSteps.WaitForIslandAsync(tab);

        Assert.Equal(site.FirstBuild.BuildId, await StaticSiteSteps.PageBuildAsync(tab));
        Assert.Equal(b.BuildId, await BuildOnSiteAsync(tab));
        Assert.False(await tab.Locator(Banner).IsVisibleAsync());
        Assert.Equal(0, await tab.Locator(".island-state").CountAsync());
        Assert.DoesNotContain(site.OriginRequests.Skip(mark), request => request.Status >= 400);
        // The island works: a session starts from A's content under B's host.
        await tab.Locator(".pf__start").ClickAsync();
        await tab.Locator(StaticSiteSteps.Question).WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        site.AssertClean(tab);
    }

    private static async Task<string> BuildOnSiteAsync(IPage tab) =>
        await tab.EvaluateAsync<string>("async () => (await (await fetch('build.json', { cache: 'no-store' })).json()).buildId");

    [Fact]
    public Task AtoB_CachedIslandPageOfAStillBoots() => RunAtoBAsync(ServiceWorkerPolicy.Allow);

    [Fact]
    public Task AtoB_WithoutServiceWorker() => RunAtoBAsync(ServiceWorkerPolicy.Block);

    [Fact]
    public async Task AtoB_PartiallyCachedAssets()
    {
        await using IBrowserContext context = await site.NewContextAsync(serviceWorkers: ServiceWorkerPolicy.Block);
        IPage tab = await StaticSiteSteps.OpenIslandAsync(site, context, PracticePath);
        string[] runtime = await tab.EvaluateAsync<string[]>(
            """
            ([prefix]) => [...new Set(performance.getEntriesByType('resource').map(entry => entry.name))]
              .filter(name => new URL(name).pathname.startsWith(prefix + '_framework/'))
            """,
            new object[] { StaticSiteFixture.Prefix });
        string[] eligible = [.. runtime.Where(url => Eligible.IsMatch(new Uri(url).AbsolutePath[StaticSiteFixture.Prefix.Length..]))];
        Assert.True(eligible.Length > 100);
        // Half of A's runtime stays in the HTTP cache (and the page itself); the other half is gone from it. The cache is
        // emptied, A's page is loaded again while every runtime file answers 404 (only the page is stored), and then the
        // first half is fetched back into the cache.
        string[] cachedHalf = [.. eligible.Where((_, index) => index % 2 == 0)];
        string[] missingHalf = [.. eligible.Where((_, index) => index % 2 == 1)];
        await StaticSiteFixture.ExpireHttpCacheAsync(tab);
        site.Block(eligible.Select(url => new Uri(url).AbsolutePath[StaticSiteFixture.Prefix.Length..]));
        await tab.GotoAsync(site.Url(PracticePath).ToString());
        await tab.WaitForLoadStateAsync(LoadState.Load);
        site.Unblock();
        await tab.EvaluateAsync(
            "async urls => { await Promise.all(urls.map(url => fetch(url).then(response => { if (!response.ok) throw new Error(url); return response.arrayBuffer(); }))); }",
            cachedHalf);

        StaticBuild b = await site.GetSecondBuildAsync();
        await site.SwapToAsync(b.Directory);
        int mark = site.OriginRequests.Count;
        await tab.GotoAsync(site.Url(PracticePath).ToString());
        await StaticSiteSteps.WaitForIslandAsync(tab);

        Assert.Equal(site.FirstBuild.BuildId, await StaticSiteSteps.PageBuildAsync(tab));
        string[] fetched = OriginPathsSince(mark);
        // The cached half never reached the origin; the other half came from B's retained copies of A's files.
        Assert.Empty(cachedHalf.Select(url => new Uri(url).AbsolutePath[StaticSiteFixture.Prefix.Length..]).Intersect(fetched, StringComparer.Ordinal));
        string[] bootFiles = [.. missingHalf.Select(url => new Uri(url).AbsolutePath[StaticSiteFixture.Prefix.Length..]).Where(path => !path.EndsWith(".br", StringComparison.Ordinal))];
        Assert.NotEmpty(bootFiles.Intersect(fetched, StringComparer.Ordinal));
        Assert.DoesNotContain(site.OriginRequests.Skip(mark), request => request.Status >= 400);
        Assert.Equal(0, await tab.Locator(".island-state").CountAsync());
        site.AssertClean(tab);
    }

    [Fact]
    public async Task CodeUpdate_OldTabOpen_GetsItsOwnBytes()
    {
        await using IBrowserContext context = await site.NewContextAsync();
        IPage oldTab = await StaticSiteSteps.OpenIslandAsync(site, context, PracticePath);
        await StaticSiteSteps.WaitForServiceWorkerControlAsync(oldTab);
        await oldTab.EvaluateAsync("() => { window.__takeovers = 0; navigator.serviceWorker.addEventListener('controllerchange', () => { window.__takeovers++; }); }");
        string aTitle = "What does the code print when lambdas capture a for loop variable?";

        StaticBuild b = await site.GetSecondBuildAsync();
        await site.SwapToAsync(b.Directory);
        IPage newTab = await site.NewPageAsync(context);
        // Another URL, so that this is B's page and not the cached copy of A's.
        await newTab.GotoAsync(site.Url(PracticePath + "?visit=b").ToString());
        await StaticSiteSteps.WaitForIslandAsync(newTab);
        Assert.Equal(b.BuildId, await StaticSiteSteps.PageBuildAsync(newTab));
        // A browser looks for a new worker on its own schedule; asking at once makes the test independent of it.
        await newTab.EvaluateAsync("async () => { await (await navigator.serviceWorker.ready).update(); }");

        // B's worker takes over the tab that A opened.
        await oldTab.WaitForFunctionAsync("() => window.__takeovers >= 1", null, new PageWaitForFunctionOptions { Timeout = Wait, PollingInterval = 100 });

        // The old tab goes on with A's bytes: the question it picks is A's version, from the cache or from A's retained file.
        int mark = site.OriginRequests.Count;
        await oldTab.Locator(".pf input[name='topic'][value='csharp']").CheckAsync();
        await oldTab.Locator(".pf__start").ClickAsync();
        await oldTab.Locator(StaticSiteSteps.Question).WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        Assert.Equal(aTitle, (await oldTab.Locator(".qcard__title").InnerTextAsync()).Trim());
        Assert.Equal(0, await oldTab.Locator(".island-state").CountAsync());
        Assert.DoesNotContain(site.OriginRequests.Skip(mark), request => request.Status >= 400);

        // B's own visits, after the HTTP cache ran out, are answered by the worker alone.
        await StaticSiteFixture.ExpireHttpCacheAsync(newTab);
        foreach (string visit in new[] { "c", "d" })
        {
            int before = site.OriginRequests.Count;
            await newTab.GotoAsync(site.Url(PracticePath + "?visit=" + visit).ToString());
            await StaticSiteSteps.WaitForIslandAsync(newTab);
            string[] fetched = OriginPathsSince(before);
            Assert.DoesNotContain(fetched, path => Eligible.IsMatch(path));
            Assert.Equal(b.BuildId, await StaticSiteSteps.PageBuildAsync(newTab));
        }

        site.AssertClean(oldTab);
        site.AssertClean(newTab);
    }

    [Fact]
    public async Task AtoB_DeletedQuestionShowsSiteUpdated()
    {
        await using IBrowserContext context = await site.NewContextAsync(serviceWorkers: ServiceWorkerPolicy.Block);
        IPage tab = await StaticSiteSteps.OpenIslandAsync(site, context, PracticePath);
        ContentWorkspace content = ContentWorkspace.CopyOfWeb();
        content.Delete("dotnet/csharp/closure-for-loop.yaml");
        content.Write("dotnet/redirects.yaml", "[]\n");
        StaticBuild b = await site.ExportBuildAsync(content);
        await site.SwapToAsync(b.Directory);
        string shard = Directory.EnumerateFiles(Path.Combine(site.Directory, "data", "en", "q"), StaticSiteSteps.ClosureId + ".*.json").Select(Path.GetFileName).Single()!;
        // Retention keeps A's copy for one deployment; once it is past, the question's file is gone.
        await site.RemoveFromServedAsync("data/en/q/" + shard);

        await tab.Locator(".pf input[name='topic'][value='csharp']").CheckAsync();
        await tab.Locator(".pf__start").ClickAsync();

        ILocator state = tab.Locator(".island-state");
        await state.WaitForAsync(new LocatorWaitForOptions { Timeout = Wait });
        Assert.Equal(StaticSiteSteps.Resource("State.SiteUpdated"), (await state.Locator("p").InnerTextAsync()).Trim());
        Assert.Equal(StaticSiteSteps.Resource("State.Reload"), (await state.Locator("button").InnerTextAsync()).Trim());
        Assert.Equal(0, await tab.Locator(".qcard").CountAsync());
        site.AssertRequestsStayUnderPrefix(tab);
        site.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Recovery_BlockedAnalyticsScript_ShowsNoBanner()
    {
        await using IBrowserContext context = await site.NewContextAsync(serviceWorkers: ServiceWorkerPolicy.Block);
        IPage tab = await site.NewPageAsync(context);
        await tab.RouteAsync("**/js/goatcounter*.js", route => route.AbortAsync("blockedbyclient"));

        await tab.GotoAsync(site.Url(PracticePath).ToString());
        await StaticSiteSteps.WaitForIslandAsync(tab);
        await tab.WaitForLoadStateAsync(LoadState.Load);

        // A blocked counter is not a broken page: its script carries no recover marker.
        Assert.Equal(0, await tab.Locator("script[src*='goatcounter'][data-recover]").CountAsync());
        Assert.Contains(site.RequestUrls(tab), url => url.Contains("/js/goatcounter.", StringComparison.Ordinal));
        Assert.False(await tab.Locator(Banner).IsVisibleAsync());
        Assert.Equal(0, await tab.Locator(".island-state").CountAsync());
        site.AssertClean(tab);
    }

    [Fact]
    public async Task Recovery_LinkKeepsTheQuery()
    {
        await using IBrowserContext context = await site.NewContextAsync(serviceWorkers: ServiceWorkerPolicy.Block);
        IPage tab = await site.NewPageAsync(context);
        string stylesheet = StaticSiteSteps.FingerprintedFile(site.FirstBuild, "css", "site", ".css");

        await OpenWithMissingAsync(tab, "en/dotnet/questions/?topic=async", stylesheet);
        await AssertBannerVisibleAsync(tab);

        Uri link = new((await Reload(tab).GetAttributeAsync("href"))!);
        System.Collections.Specialized.NameValueCollection query = System.Web.HttpUtility.ParseQueryString(link.Query);
        Assert.Equal(site.Url("en/dotnet/questions/").AbsolutePath, link.AbsolutePath);
        Assert.Equal("async", query["topic"]);
        Assert.Matches("^[0-9]{10,}$", query["_r"]);
        Assert.StartsWith(site.BaseAddress.GetLeftPart(UriPartial.Authority), link.ToString(), StringComparison.Ordinal);
        site.AssertClean(tab);
    }

    [Fact]
    public async Task Recovery_ShownOncePerPageAndSession()
    {
        await using IBrowserContext context = await site.NewContextAsync(serviceWorkers: ServiceWorkerPolicy.Block);
        IPage tab = await site.NewPageAsync(context);
        string stylesheet = StaticSiteSteps.FingerprintedFile(site.FirstBuild, "css", "site", ".css");
        await OpenWithMissingAsync(tab, "en/dotnet/questions/", stylesheet);
        await AssertBannerVisibleAsync(tab);

        // The same URL fails again in the same session: no banner this time, so a broken file cannot make the page flicker.
        // The failure is proven first: the stylesheet really failed on the second load (a 404 from the host or, as Pages sends
        // max-age on its 404s too, from the HTTP cache). Without it a stylesheet that loaded would leave the page healthy and
        // the check below vacuous.
        List<int> stylesheetStatuses = [];
        tab.Response += (_, response) =>
        {
            if (response.Url.EndsWith(stylesheet, StringComparison.Ordinal))
            {
                lock (stylesheetStatuses)
                {
                    stylesheetStatuses.Add(response.Status);
                }
            }
        };
        await tab.GotoAsync(site.Url("en/dotnet/questions/").ToString());
        await tab.WaitForLoadStateAsync(LoadState.Load);
        lock (stylesheetStatuses)
        {
            Assert.Equal([404], stylesheetStatuses);
        }

        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.False(await tab.Locator(Banner).IsVisibleAsync());

        // Another URL is another page: it gets its own notice.
        await tab.GotoAsync(site.Url("en/dotnet/questions/?other=1").ToString());
        await AssertBannerVisibleAsync(tab);
        site.AssertClean(tab);
    }

    [Fact]
    public async Task Missing_StartupScript_ShowsRecovery()
    {
        await using IBrowserContext context = await site.NewContextAsync(serviceWorkers: ServiceWorkerPolicy.Block);
        IPage tab = await site.NewPageAsync(context);

        await OpenWithMissingAsync(tab, PracticePath, StaticSiteSteps.FingerprintedFile(site.FirstBuild, "js", "islands-start", ".js"));

        await AssertBannerVisibleAsync(tab);
        Assert.Contains("data-reload", await tab.Locator(Banner).InnerHTMLAsync(), StringComparison.Ordinal);
        site.AssertClean(tab);
    }

    [Fact]
    public async Task Missing_BootModule_ShowsRecovery()
    {
        await using IBrowserContext context = await site.NewContextAsync(serviceWorkers: ServiceWorkerPolicy.Block);
        IPage tab = await site.NewPageAsync(context);

        await OpenWithMissingAsync(tab, PracticePath, StaticSiteSteps.FingerprintedFile(site.FirstBuild, "_framework", "dotnet", ".js"));

        await AssertBannerVisibleAsync(tab);
        await AssertBootErrorShownAsync(tab);
        site.AssertClean(tab);
    }

    [Fact]
    public async Task Missing_Assembly_ShowsRecovery()
    {
        await using IBrowserContext context = await site.NewContextAsync(serviceWorkers: ServiceWorkerPolicy.Block);
        IPage tab = await site.NewPageAsync(context);
        string assembly = StaticSiteSteps.FingerprintedFile(site.FirstBuild, "_framework", "Trainer.Web.Client", ".wasm");

        // The compressed copy goes too: the loader falls back to the plain file, and both are gone.
        await OpenWithMissingAsync(tab, PracticePath, assembly, assembly + ".br");

        await AssertBannerVisibleAsync(tab);
        await AssertBootErrorShownAsync(tab);
        site.AssertClean(tab);
    }

    [Fact]
    public async Task Missing_Stylesheet_ShowsRecovery()
    {
        await using IBrowserContext context = await site.NewContextAsync(serviceWorkers: ServiceWorkerPolicy.Block);
        IPage tab = await site.NewPageAsync(context);

        await OpenWithMissingAsync(tab, "en/about/", StaticSiteSteps.FingerprintedFile(site.FirstBuild, "css", "site", ".css"));

        await AssertBannerVisibleAsync(tab);
        site.AssertClean(tab);
    }

    [Fact]
    public async Task Recovery_ReloadFetchesFreshHtml()
    {
        await using IBrowserContext context = await site.NewContextAsync(serviceWorkers: ServiceWorkerPolicy.Block);
        IPage tab = await site.NewPageAsync(context);
        string pageUrl = site.Url("en/dotnet/questions/").ToString();
        await tab.GotoAsync(pageUrl);
        string stylesheet = StaticSiteSteps.FingerprintedFile(site.FirstBuild, "css", "site", ".css");
        // Only the HTML stays in the HTTP cache: A's page is cached, its stylesheet is not.
        await StaticSiteFixture.ExpireHttpCacheAsync(tab);
        await tab.EvaluateAsync("async url => { await (await fetch(url)).text(); }", pageUrl);
        StaticBuild b = await site.GetSecondBuildAsync();
        await site.SwapToAsync(b.Directory);
        // A's stylesheet is past its retention window.
        await site.RemoveFromServedAsync(stylesheet);

        await tab.GotoAsync(pageUrl);
        await AssertBannerVisibleAsync(tab);
        Assert.Equal(site.FirstBuild.BuildId, await StaticSiteSteps.PageBuildAsync(tab));

        await Reload(tab).ClickAsync();
        await tab.WaitForURLAsync(url => url.Contains("_r=", StringComparison.Ordinal));
        await tab.WaitForLoadStateAsync(LoadState.Load);

        Assert.Equal(b.BuildId, await StaticSiteSteps.PageBuildAsync(tab));
        Assert.False(await tab.Locator(Banner).IsVisibleAsync());
        Assert.NotEqual(0, await tab.Locator("ol.rows .row").CountAsync());
        site.AssertClean(tab);
    }
}
