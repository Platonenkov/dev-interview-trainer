using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Trainer.Application.Catalog;
using Trainer.Domain.Keys;
using Trainer.Web.Catalog;
using Trainer.Web.ContentState;

namespace Trainer.Web.E2E;

/// <summary>
/// The static catalog renders every row and <c>catalog-filter.js</c> hides the ones a query string does not select. The
/// server edition answers the same query from the database; the two must agree on every spelling of a selection, so each
/// case here is held to what <see cref="ICatalogQueries"/> returns for the query <see cref="CatalogQueryParser"/> reads.
/// </summary>
[Trait("Category", "E2E")]
public sealed class StaticCatalogFilterTests(StaticSiteFixture site) : IClassFixture<StaticSiteFixture>, IAsyncLifetime
{
    // The web fixture's topics are async, concurrency and csharp, plus generics, which is known and has no question: a topic
    // the page has no checkbox for but the server still counts as a selection that matches nothing.
    public static TheoryData<string, string> CatalogCases => new()
    {
        { "en", "" },
        { "en", "?level=junior" },
        { "en", "?level=junior&level=middle" },
        { "en", "?level=JUNIOR" },
        { "en", "?level=middle&topic=async" },
        { "en", "?topic=async&topic=concurrency" },
        { "en", "?topic=async&topic=concurrency&level=middle&type=open" },
        { "en", "?topic=generics" },
        { "en", "?topic=generics&topic=csharp" },
        { "en", "?topic=unknown" },
        { "en", "?topic=unknown&level=junior" },
        { "en", "?type=output&type=bogus" },
        { "en", "?level=bogus" },
        { "en", "?q=await" },
        { "en", "?q=%20%20AWAIT%20%20" },
        { "en", "?q=await&level=middle" },
        { "en", "?q=interlocked" },
        { "en", "?q=Interlocked" },
        { "en", "?q=interlock" },
        { "en", "?q=synchronization%20context" },
        { "en", "?q=winforms%20button" },
        { "en", "?q=deadlock&topic=async" },
        { "en", "?q=%00await%09" },
        { "en", "?q=" },
        { "en", "?q=%20%20" },
        { "en", "?page=2&level=middle" },
        { "en", "?_r=1234567890&level=junior" },
        { "en", "?q=a&q=await" },
        { "ru", "" },
        { "ru", "?level=middle&topic=async" },
        { "ru", "?q=%D0%97%D0%B0%D0%BC%D1%8B%D0%BA%D0%B0%D0%BD%D0%B8%D1%8F" },
        { "ru", "?q=%D0%97%D0%90%D0%9C%D0%AB%D0%9A%D0%90%D0%9D%D0%98%D0%AF" },
        { "ru", "?q=closures" },
    };

    public ValueTask InitializeAsync() => new(site.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<IPage> OpenAsync(IBrowserContext context, string culture, string query)
    {
        IPage tab = await site.NewPageAsync(context);
        await tab.GotoAsync(site.Url($"{culture}/dotnet/questions/{query}").ToString());
        await tab.Locator("[data-found-template]").WaitForAsync();
        return tab;
    }

    private static async Task<string[]> VisibleSlugsAsync(IPage tab) =>
        await tab.EvaluateAsync<string[]>(
            "() => [...document.querySelectorAll('ol.rows > li')].filter(li => !li.hidden).map(li => li.querySelector('a.row__title').getAttribute('href').split('/').filter(Boolean).pop())");

    private static async Task<string[]> CheckedAsync(IPage tab, string name) =>
        await tab.EvaluateAsync<string[]>(
            "name => [...document.querySelectorAll(`input[type=checkbox][name=${name}]:checked`)].map(box => box.value)",
            name);

    // The rows of the query as the server edition would list them, across all of its pages.
    private async Task<(string[] Slugs, int Total)> ServerRowsAsync(string culture, string query)
    {
        await using AsyncServiceScope scope = site.Services.CreateAsyncScope();
        ICatalogQueries catalogs = scope.ServiceProvider.GetRequiredService<ICatalogQueries>();
        ContentSnapshotProvider snapshots = site.Services.GetRequiredService<ContentSnapshotProvider>();
        HashSet<TopicKey> known = [.. snapshots.Current.TopicsOf("dotnet").Select(topic => new TopicKey(topic))];
        QueryCollection collection = new(QueryHelpers.ParseQuery(query));
        CatalogQuery parsed = CatalogQueryParser.Parse(new CultureKey(culture), new StackKey("dotnet"), collection, known);

        List<string> slugs = [];
        int total;
        int page = 1;
        while (true)
        {
            CatalogPage result = await catalogs.GetAsync(parsed with { Page = page }, TestContext.Current.CancellationToken);
            slugs.AddRange(result.Rows.Select(row => row.Slug));
            total = result.Total;
            if (page >= result.PageCount)
            {
                break;
            }

            page++;
        }

        return ([.. slugs], total);
    }

    [Fact]
    public async Task QueryStringFiltersRows()
    {
        await using IBrowserContext context = await site.NewContextAsync(serviceWorkers: ServiceWorkerPolicy.Block);
        const string query = "?level=middle&topic=async&q=configure";
        IPage tab = await OpenAsync(context, "en", query);

        (string[] expected, int total) = await ServerRowsAsync("en", query);

        Assert.NotEmpty(expected);
        // The query narrows the list: the page holds more rows than the selection shows.
        Assert.True(expected.Length < await tab.Locator("ol.rows > li").CountAsync());
        Assert.Equal(expected, await VisibleSlugsAsync(tab));
        Assert.Equal($"Found: {total}", (await tab.Locator("[data-found-template]").InnerTextAsync()).Trim());
        Assert.Equal(["middle"], await CheckedAsync(tab, "level"));
        Assert.Equal(["async"], await CheckedAsync(tab, "topic"));
        Assert.Equal("configure", await tab.Locator("#catalog-search").InputValueAsync());
        Assert.True(await tab.Locator("details.filters").EvaluateAsync<bool>("details => details.open"));
        site.AssertClean(tab);
    }

    [Fact]
    public async Task JunkQueryShowsTheNormalisedSelection()
    {
        await using IBrowserContext context = await site.NewContextAsync(serviceWorkers: ServiceWorkerPolicy.Block);
        IPage junk = await OpenAsync(context, "en", "?level=JUNIOR&level=junior&topic=unknown");
        IPage clean = await OpenAsync(context, "en", "?level=junior");

        Assert.Equal(["junior"], await CheckedAsync(junk, "level"));
        Assert.Empty(await CheckedAsync(junk, "topic"));
        Assert.Empty(await CheckedAsync(junk, "type"));
        Assert.Equal(await VisibleSlugsAsync(clean), await VisibleSlugsAsync(junk));
        Assert.Equal((await ServerRowsAsync("en", "?level=junior")).Slugs, await VisibleSlugsAsync(junk));
        Assert.NotEmpty(await VisibleSlugsAsync(junk));
        site.AssertClean(junk);
        site.AssertClean(clean);
    }

    [Fact]
    public async Task CatalogSearch_MatchesTitleSubstringOrExactTag()
    {
        await using IBrowserContext context = await site.NewContextAsync(serviceWorkers: ServiceWorkerPolicy.Block);

        // A title containing the term, in any case.
        IPage title = await OpenAsync(context, "en", "?q=AWAIT");
        string[] byTitle = await VisibleSlugsAsync(title);
        Assert.NotEmpty(byTitle);
        Assert.Contains("configureawait-false-purpose", byTitle);
        Assert.DoesNotContain("parallel-counter-race", byTitle);

        // A tag spelt exactly: parallel-counter-race is tagged interlocked and has no such word in its title...
        IPage exact = await OpenAsync(context, "en", "?q=interlocked");
        Assert.Equal(["parallel-counter-race"], await VisibleSlugsAsync(exact));

        // ...but a prefix of a tag is neither a tag nor part of any title.
        IPage prefix = await OpenAsync(context, "en", "?q=interlock");
        Assert.Empty(await VisibleSlugsAsync(prefix));
        Assert.False(await prefix.Locator("[data-catalog-empty]").IsHiddenAsync());
        Assert.Equal("Found: 0", (await prefix.Locator("[data-found-template]").InnerTextAsync()).Trim());

        // Spaces in the term become hyphens when it is read as a tag.
        IPage hyphen = await OpenAsync(context, "en", "?q=synchronization%20context");
        string[] byTag = await VisibleSlugsAsync(hyphen);
        Assert.Contains("result-deadlock-ui-thread", byTag);
        Assert.Equal((await ServerRowsAsync("en", "?q=synchronization%20context")).Slugs, byTag);

        foreach (IPage tab in new[] { title, exact, prefix, hyphen })
        {
            site.AssertClean(tab);
        }
    }

    [Theory]
    [MemberData(nameof(CatalogCases))]
    public async Task CatalogFilter_MatchesServerCatalogForEveryCase(string culture, string query)
    {
        await using IBrowserContext context = await site.NewContextAsync(locale: culture == "ru" ? "ru-RU" : "en-US", serviceWorkers: ServiceWorkerPolicy.Block);
        IPage tab = await OpenAsync(context, culture, query);

        (string[] expected, int total) = await ServerRowsAsync(culture, query);

        string[] visible = await VisibleSlugsAsync(tab);
        Assert.Equal(expected, visible);
        string found = (await tab.Locator("[data-found-template]").InnerTextAsync()).Trim();
        Assert.EndsWith(": " + total.ToString(System.Globalization.CultureInfo.InvariantCulture), found, StringComparison.Ordinal);
        Assert.Equal(total == 0, !await tab.Locator("[data-catalog-empty]").IsHiddenAsync());
        site.AssertClean(tab);
    }
}
