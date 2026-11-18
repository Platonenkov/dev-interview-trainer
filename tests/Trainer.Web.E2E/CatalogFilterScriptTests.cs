using Microsoft.Playwright;

namespace Trainer.Web.E2E;

/// <summary>
/// Runs <c>catalog-filter.js</c> in a browser against a hand-built copy of the static catalog's markup, with the query
/// string carried by the real URL. The server rules it mirrors are held to <c>ICatalogQueries</c> by the parity test
/// of the static edition; these cover what that table does not: the opened drawer and a topic that is not on the page.
/// </summary>
[Trait("Category", "E2E")]
public sealed class CatalogFilterScriptTests(AppHostFixture host) : IClassFixture<AppHostFixture>
{
    private const string Markup = """
        <form role="search" data-catalog-filter data-known-topics="async csharp empty-topic">
          <input name="q" type="search">
          <details class="filters"><summary>Filters</summary>
            <label><input type="checkbox" name="level" value="junior"></label>
            <label><input type="checkbox" name="level" value="middle"></label>
            <label><input type="checkbox" name="topic" value="async"></label>
            <label><input type="checkbox" name="topic" value="csharp"></label>
          </details>
        </form>
        <p data-found-template="Found: {0}">Found: 2</p>
        <p data-catalog-empty hidden>None</p>
        <ol>
          <li id="a"><div data-level="junior" data-topic="csharp" data-type="open" data-title="closures" data-tags="lambda"></div></li>
          <li id="b"><div data-level="middle" data-topic="async" data-type="open" data-title="await" data-tags=""></div></li>
        </ol>
        """;

    private static string Script()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DevInterviewTrainer.slnx")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllText(Path.Combine(directory!.FullName, "src", "Trainer.Web", "wwwroot", "js", "catalog-filter.js"));
    }

    private async Task<IPage> RunAsync(string query)
    {
        // BypassCSP: the page's policy forbids inline script, and the script text is the shipped file's.
        IBrowserContext context = await host.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString(),
            BypassCSP = true,
        });
        IPage tab = await host.NewPageAsync(context);
        await tab.GotoAsync("/en/about" + query);
        await tab.EvaluateAsync("html => { document.body.innerHTML = html; }", Markup);
        await tab.AddScriptTagAsync(new PageAddScriptTagOptions { Content = Script() });
        return tab;
    }

    private static async Task<string[]> VisibleAsync(IPage tab) =>
        await tab.EvaluateAsync<string[]>("() => [...document.querySelectorAll('li')].filter(l => !l.hidden).map(l => l.id)");

    [Fact]
    public async Task CheckedFilterFromTheUrl_OpensTheDrawer()
    {
        IPage tab = await RunAsync("?level=middle");

        Assert.True(await tab.EvaluateAsync<bool>("() => document.querySelector('details').open"));
        Assert.Equal(["b"], await VisibleAsync(tab));
    }

    [Fact]
    public async Task NoFilterInTheUrl_LeavesTheDrawerClosed()
    {
        IPage tab = await RunAsync("?q=closures");

        Assert.False(await tab.EvaluateAsync<bool>("() => document.querySelector('details').open"));
        Assert.Equal(["a"], await VisibleAsync(tab));
    }

    [Fact]
    public async Task KnownTopicWithoutLiveQuestions_ShowsNoRowsAndTheEmptyState()
    {
        IPage tab = await RunAsync("?topic=Empty-Topic");

        Assert.Empty(await VisibleAsync(tab));
        Assert.False(await tab.EvaluateAsync<bool>("() => document.querySelector('[data-catalog-empty]').hidden"));
        Assert.Equal("Found: 0", await tab.EvaluateAsync<string>("() => document.querySelector('[data-found-template]').textContent"));
    }

    [Fact]
    public async Task UnknownTopic_IsIgnoredLikeTheServerDoes()
    {
        IPage tab = await RunAsync("?topic=nope");

        Assert.Equal(["a", "b"], await VisibleAsync(tab));
    }
}
