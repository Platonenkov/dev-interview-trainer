using Microsoft.Playwright;
using Trainer.TestSupport;

namespace Trainer.Web.E2E;

[Trait("Category", "E2E")]
public sealed class LayoutTests(AppHostFixture host) : IClassFixture<AppHostFixture>
{
    private const int PhoneWidth = 375;

    private static readonly string ScreenshotDirectory = Path.Combine(ContentWorkspace.RepositoryRoot(), "TestResults", "screenshots");

    private static readonly Dictionary<string, string> Pages = new(StringComparer.Ordinal)
    {
        ["home"] = "/en",
        ["about"] = "/en/about",
        ["catalog"] = "/en/dotnet/questions?level=middle",
        ["catalog-search"] = "/en/dotnet/questions?level=middle&q=ConfigureAwait",
        ["topic"] = "/en/dotnet/topics/concurrency",
        ["question"] = "/en/dotnet/questions/parallel-counter-race",
        ["not-found"] = "/en/dotnet/questions/no-such-question",
    };

    public static TheoryData<string, int, string> LayoutMatrix()
    {
        TheoryData<string, int, string> data = [];
        foreach (string page in Pages.Keys)
        {
            foreach (int width in new[] { 375, 768, 1280, 1920 })
            {
                data.Add(page, width, "dark");
            }

            foreach (int width in new[] { 375, 1280 })
            {
                data.Add(page, width, "light");
            }
        }

        // The narrowest supported width, where an unbreakable word or a code line is most likely to push the page sideways.
        data.Add("home", 320, "dark");
        data.Add("catalog-search", 320, "dark");
        data.Add("question", 320, "dark");
        return data;
    }

    private async Task<IBrowserContext> NewContextAsync(int width, string theme)
    {
        IBrowserContext context = await host.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = width, Height = 900 },
            BaseURL = host.BaseAddress.ToString(),
        });
        await context.AddInitScriptAsync($"try {{ localStorage.setItem('theme', '{theme}'); }} catch (e) {{ }}");
        return context;
    }

    [Theory]
    [MemberData(nameof(LayoutMatrix))]
    public async Task NoHorizontalOverflow(string page, int width, string theme)
    {
        await using IBrowserContext context = await NewContextAsync(width, theme);
        IPage tab = await context.NewPageAsync();
        await tab.GotoAsync(Pages[page]);
        await tab.EvaluateAsync("document.fonts.ready");

        // Measured before the full-page screenshot, which resizes the viewport while it captures.
        int scrollWidth = await tab.EvaluateAsync<int>("document.documentElement.scrollWidth");
        int innerWidth = await tab.EvaluateAsync<int>("window.innerWidth");

        Directory.CreateDirectory(ScreenshotDirectory);
        await tab.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = Path.Combine(ScreenshotDirectory, $"{page}-{width}-{theme}.png"),
            FullPage = true,
        });

        Assert.True(scrollWidth <= innerWidth, $"{page} at {width} px ({theme}): scrollWidth {scrollWidth} > innerWidth {innerWidth}.");
    }

    [Fact]
    public async Task CodePanelScrollsInternally()
    {
        await using IBrowserContext context = await NewContextAsync(PhoneWidth, "dark");
        IPage tab = await context.NewPageAsync();
        await tab.GotoAsync(Pages["question"]);

        ILocator panel = tab.Locator("pre.code").First;
        int panelScroll = await panel.EvaluateAsync<int>("e => e.scrollWidth");
        int panelClient = await panel.EvaluateAsync<int>("e => e.clientWidth");
        int scrollWidth = await tab.EvaluateAsync<int>("document.documentElement.scrollWidth");

        Assert.True(panelScroll > panelClient, $"The code panel does not scroll: scrollWidth {panelScroll}, clientWidth {panelClient}.");
        Assert.True(scrollWidth <= PhoneWidth, $"The page scrolls sideways: scrollWidth {scrollWidth}.");
    }

    [Fact]
    public async Task FilterDrawerWorksOnPhone()
    {
        await using IBrowserContext context = await NewContextAsync(PhoneWidth, "dark");
        IPage tab = await context.NewPageAsync();
        await tab.GotoAsync(Pages["catalog"]);

        ILocator drawer = tab.Locator("details.filters");
        ILocator levelLinks = drawer.Locator("a[href*='level=']");
        Assert.False(await drawer.EvaluateAsync<bool>("e => e.open"));
        Assert.False(await levelLinks.First.IsVisibleAsync());

        await drawer.Locator("summary").ClickAsync();

        Assert.True(await drawer.EvaluateAsync<bool>("e => e.open"));
        Assert.True(await levelLinks.First.IsVisibleAsync());
        Assert.True(await levelLinks.CountAsync() >= 2);
    }

    [Fact]
    public async Task FiltersAreAlwaysVisibleOnDesktop()
    {
        await using IBrowserContext context = await NewContextAsync(1280, "dark");
        IPage tab = await context.NewPageAsync();
        await tab.GotoAsync(Pages["catalog"]);

        Assert.True(await tab.Locator("details.filters a[href*='level=']").First.IsVisibleAsync());
        Assert.False(await tab.Locator("details.filters summary").IsVisibleAsync());
    }

    [Theory]
    [InlineData(375)]
    [InlineData(768)]
    [InlineData(1023)]
    public async Task TouchTargetsAtLeast44px_IncludingDrawerThemeAndLanguageControls(int width)
    {
        await using IBrowserContext context = await NewContextAsync(width, "dark");
        IPage tab = await context.NewPageAsync();
        await tab.GotoAsync(Pages["catalog"]);

        // The drawer must be opened first: its links have no box while it is closed, and a skipped target proves nothing.
        await tab.Locator("details.filters summary").ClickAsync();
        Assert.True(await tab.Locator("details.filters").EvaluateAsync<bool>("e => e.open"));

        await AssertTargetsAsync(tab, "#theme-toggle", "theme toggle");
        await AssertTargetsAsync(tab, ".lang-switch a", "language switch");
        await AssertTargetsAsync(tab, ".filters summary", "filter drawer toggle");
        await AssertTargetsAsync(tab, ".filters a", "filter drawer links");
        await AssertTargetsAsync(tab, "header a, header button", "header controls");
    }

    [Theory]
    [InlineData(320)]
    [InlineData(375)]
    [InlineData(768)]
    [InlineData(1280)]
    [InlineData(1920)]
    public async Task SearchFormTargetsAtLeast44pxAndTheFormFitsTheViewport(int width)
    {
        await using IBrowserContext context = await NewContextAsync(width, "dark");
        IPage tab = await context.NewPageAsync();
        await tab.GotoAsync(Pages["catalog-search"]);

        await AssertTargetsAsync(tab, ".catalog-search input[type=search]", "search box");
        await AssertTargetsAsync(tab, ".catalog-search button", "search button");
        await AssertTargetsAsync(tab, ".catalog-search__clear", "clear search link");

        // The form is visible without opening the filter drawer and stays inside the viewport at every width.
        LocatorBoundingBoxResult? form = await tab.Locator("form.catalog-search").BoundingBoxAsync();
        Assert.NotNull(form);
        Assert.True(form.X >= 0 && form.X + form.Width <= width, $"The search form spans {form.X:0.#}..{form.X + form.Width:0.#} in a {width} px viewport.");
        Assert.True(await tab.Locator("form.catalog-search input[type=search]").IsVisibleAsync());
        Assert.Equal("ConfigureAwait", await tab.Locator("#catalog-search").InputValueAsync());
    }

    private static async Task AssertTargetsAsync(IPage tab, string selector, string description)
    {
        IReadOnlyList<ILocator> targets = await tab.Locator(selector).AllAsync();
        Assert.True(targets.Count > 0, $"No {description} found for '{selector}'.");

        List<string> problems = [];
        foreach (ILocator target in targets)
        {
            string label = (await target.InnerTextAsync()).Trim();
            LocatorBoundingBoxResult? box = await target.BoundingBoxAsync();
            if (box is null)
            {
                problems.Add($"'{label}' is not rendered");
            }
            else if (box.Width < 44 || box.Height < 44)
            {
                problems.Add($"'{label}' {box.Width:0.#}x{box.Height:0.#}");
            }
        }

        Assert.True(problems.Count == 0, $"{description}: targets below 44x44 or hidden: " + string.Join("; ", problems));
    }

    [Fact]
    public async Task ThemeAppliedBeforeFirstPaint()
    {
        await using IBrowserContext context = await host.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = host.BaseAddress.ToString() });
        await context.AddInitScriptAsync("try { localStorage.setItem('theme', 'light'); } catch (e) { }");
        // Records data-theme at the moment the parser inserts <body>, which is before anything is painted.
        await context.AddInitScriptAsync("""
            new MutationObserver(function (records, observer) {
              for (var i = 0; i < records.length; i++) {
                for (var j = 0; j < records[i].addedNodes.length; j++) {
                  if (records[i].addedNodes[j].nodeName === 'BODY') {
                    window.__themeAtBody = document.documentElement.dataset.theme || '';
                    observer.disconnect();
                    return;
                  }
                }
              }
            }).observe(document, { childList: true, subtree: true });
            """);
        IPage tab = await context.NewPageAsync();
        await tab.GotoAsync("/en");

        Assert.Equal("light", await tab.EvaluateAsync<string>("window.__themeAtBody"));
    }

    [Fact]
    public async Task ThemePersistsAcrossNavigation()
    {
        await using IBrowserContext context = await host.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString(),
            ColorScheme = ColorScheme.Dark,
        });
        IPage tab = await context.NewPageAsync();
        await tab.GotoAsync("/en");
        Assert.Equal("dark", await tab.EvaluateAsync<string>("document.documentElement.dataset.theme"));

        await tab.Locator("#theme-toggle").ClickAsync();
        Assert.Equal("light", await tab.EvaluateAsync<string>("document.documentElement.dataset.theme"));

        await tab.Locator("a[href^='/en/dotnet/questions']").First.ClickAsync();
        await tab.WaitForURLAsync("**/questions**");

        Assert.Equal("light", await tab.EvaluateAsync<string>("document.documentElement.dataset.theme"));
    }
}
