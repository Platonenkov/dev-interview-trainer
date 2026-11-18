using System.Collections.Concurrent;
using Microsoft.Playwright;
using Trainer.TestSupport;

namespace Trainer.Web.E2E;

/// <summary>
/// The boundary between public pages and island pages (ADR 0009): crossing it is always a full page load, so every
/// island document starts its own WebAssembly runtime exactly once, and public pages never load the runtime.
/// Document identity is proven with a marker set on <c>window</c> before each click: a new document does not have it.
/// </summary>
[Trait("Category", "E2E")]
public sealed class IslandNavigationTests(AppHostFixture host) : IClassFixture<AppHostFixture>
{
    private const string PracticePath = "/en/dotnet/practice";
    private const string ReadyIsland = ".island-root[data-island-ready='true']";

    private async Task<IBrowserContext> NewContextAsync() =>
        await host.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = host.BaseAddress.ToString(), Locale = "en-US" });

    private static async Task WaitForIslandAsync(IPage tab) =>
        await tab.Locator(ReadyIsland).WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });

    private static async Task<string> MarkDocumentAsync(IPage tab)
    {
        string marker = Guid.NewGuid().ToString("N");
        await tab.EvaluateAsync("m => { window.__ditDocId = m; }", marker);
        return marker;
    }

    private static async Task AssertNewDocumentAsync(IPage tab) =>
        Assert.True(await tab.EvaluateAsync<bool>("() => window.__ditDocId === undefined"), "The page was not reloaded: the old document survived.");

    private static async Task<int> RuntimeStartsAsync(IPage tab) => await tab.EvaluateAsync<int>("() => window.__ditStarts || 0");

    private static async Task<string> ThemeAsync(IPage tab) => await tab.EvaluateAsync<string>("() => document.documentElement.dataset.theme");

    [Fact]
    public async Task RuntimeFailingToStart_ShowsTheReloadMessageAndHidesTheSkeleton()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await host.NewPageAsync(context);
        await tab.RouteAsync("**/_framework/blazor.web*.js", route => route.AbortAsync());

        await tab.GotoAsync(PracticePath);

        ILocator message = tab.Locator(".island-boot-error");
        await message.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60_000 });
        Assert.True(await message.Locator("a").IsVisibleAsync());
        Assert.False(await tab.Locator(".island-skeleton").IsVisibleAsync());
        Assert.Equal(0, await tab.Locator(".island-root").CountAsync());
    }

    [Fact]
    public async Task ErrorAfterASuccessfulStart_DoesNotShowTheBootFailureMessage()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await host.NewPageAsync(context);
        // Blazor.start succeeds; the callback that follows it then fails.
        await tab.AddInitScriptAsync(
            """
            (() => {
              let value;
              const late = () => { window.__lateThrown = true; throw new Error('late failure'); };
              Object.defineProperty(window, 'Blazor', {
                configurable: true,
                get() {
                  return value && new Proxy(value, { get: (target, key) => key === 'addEventListener' ? late : Reflect.get(target, key) });
                },
                set(v) { value = v; }
              });
            })();
            """);

        await tab.GotoAsync(PracticePath);
        await WaitForIslandAsync(tab);

        Assert.True(await tab.EvaluateAsync<bool>("() => window.__lateThrown === true"), "The injected failure after the start never ran.");
        Assert.False(await tab.Locator(".island-boot-error").IsVisibleAsync());
    }

    [Fact]
    public async Task IslandToPublicToIsland_EachVisitIsANewDocumentWithOneRuntimeStart()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await host.NewPageAsync(context);
        ConcurrentQueue<string> requests = new();
        tab.Request += (_, request) => requests.Enqueue(new Uri(request.Url).AbsolutePath);

        await tab.GotoAsync(PracticePath);
        await WaitForIslandAsync(tab);
        Assert.Equal(1, await RuntimeStartsAsync(tab));
        Assert.Contains(requests, path => path.StartsWith("/_framework/", StringComparison.Ordinal));

        await MarkDocumentAsync(tab);
        requests.Clear();
        await tab.Locator("a.brand").ClickAsync();
        await tab.WaitForURLAsync("**/en");
        await tab.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await AssertNewDocumentAsync(tab);
        Assert.Equal(0, await RuntimeStartsAsync(tab));
        Assert.DoesNotContain(requests, path => path.StartsWith("/_framework/", StringComparison.Ordinal));
        Assert.Equal(0, await tab.Locator("script[src*='blazor.web']").CountAsync());

        await MarkDocumentAsync(tab);
        await tab.GotoAsync(PracticePath);
        await WaitForIslandAsync(tab);
        await AssertNewDocumentAsync(tab);
        Assert.Equal(1, await RuntimeStartsAsync(tab));

        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task ThemePersistsAcrossIslandNavigation()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await host.NewPageAsync(context);
        await tab.GotoAsync("/en");
        string initial = await ThemeAsync(tab);
        await tab.Locator("#theme-toggle").ClickAsync();
        string chosen = await ThemeAsync(tab);
        Assert.NotEqual(initial, chosen);

        await tab.GotoAsync(PracticePath);
        await WaitForIslandAsync(tab);
        Assert.Equal(chosen, await ThemeAsync(tab));

        await tab.Locator("a.brand").ClickAsync();
        await tab.WaitForURLAsync("**/en");
        Assert.Equal(chosen, await ThemeAsync(tab));

        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task ThemeToggleWorksOnIslandPage()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await host.NewPageAsync(context);
        await tab.GotoAsync(PracticePath);
        await WaitForIslandAsync(tab);
        string before = await ThemeAsync(tab);

        await tab.Locator("#theme-toggle").ClickAsync();

        string after = await ThemeAsync(tab);
        Assert.NotEqual(before, after);
        Assert.Equal(after, await tab.EvaluateAsync<string>("() => localStorage.getItem('dit.theme')"));

        await tab.Locator("#theme-toggle").ClickAsync();
        Assert.Equal(before, await ThemeAsync(tab));

        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task LanguageSwitchFromIsland_IsFullReloadIntoSameMode()
    {
        await using IBrowserContext context = await NewContextAsync();
        IPage tab = await host.NewPageAsync(context);
        await tab.GotoAsync(PracticePath + "?level=junior");
        await WaitForIslandAsync(tab);
        Assert.Equal("en", await tab.Locator(ReadyIsland).GetAttributeAsync("data-island-culture"));

        await MarkDocumentAsync(tab);
        await tab.Locator(".lang-switch a[hreflang='ru']").ClickAsync();
        await tab.WaitForURLAsync("**/ru/dotnet/practice?level=junior");
        await WaitForIslandAsync(tab);

        await AssertNewDocumentAsync(tab);
        Assert.Equal("ru", await tab.EvaluateAsync<string>("() => document.documentElement.lang"));
        Assert.Equal("ru", await tab.Locator(ReadyIsland).GetAttributeAsync("data-island-culture"));
        Assert.Equal(1, await RuntimeStartsAsync(tab));

        host.AssertNoCspViolations(tab);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IslandPage_HasNoCspViolations(bool signedIn)
    {
        await using IBrowserContext context = await NewContextAsync();
        if (signedIn)
        {
            await host.SignInAsync(context, new FakeTelegramIdentity(990_100_001L, "Island Tester", "island_tester"));
        }

        IPage tab = await host.NewPageAsync(context);
        IResponse? response = await tab.GotoAsync(PracticePath);
        Assert.Equal(200, response?.Status);
        await WaitForIslandAsync(tab);
        await tab.WaitForLoadStateAsync(LoadState.NetworkIdle);

        Assert.Equal(signedIn ? "true" : "false", await tab.Locator(ReadyIsland).GetAttributeAsync("data-island-signed-in"));
        host.AssertNoCspViolations(tab);
    }
}
