using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.DependencyInjection;
using Trainer.Application.Catalog;
using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;
using Trainer.TestSupport;

namespace Trainer.Web.Tests;

/// <summary>
/// The static edition's catalog (ADR 0022): every live row on one page, filtered in the browser by catalog-filter.js,
/// next to the unchanged server catalog.
/// </summary>
public sealed class StaticCatalogTests(StaticSiteFixture site, PostgresFixture postgres) : IClassFixture<StaticSiteFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<IHtmlDocument> DocumentAsync(TrainerWebFactory factory, string path)
    {
        using HttpClient client = factory.CreateNonRedirectingClient();
        using HttpResponseMessage response = await client.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return new HtmlParser().ParseDocument(await response.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<CatalogPage> LiveAsync(TrainerWebFactory factory, string culture)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICatalogQueries>()
            .GetAllAsync(new CultureKey(culture), new StackKey("dotnet"), Ct);
    }

    private static string[] Values(IElement form, string name) =>
        [.. form.QuerySelectorAll($"input[type=checkbox][name={name}]").Select(i => i.GetAttribute("value")!)];

    private static IElement RowOf(IHtmlDocument document, string slug) =>
        document.QuerySelectorAll("ol.rows .row")
            .Single(r => r.QuerySelector("a.row__title")!.GetAttribute("href")!.EndsWith($"/{slug}/", StringComparison.Ordinal));

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public async Task StaticCatalog_ListsEveryLiveQuestionWithoutAPager(string culture)
    {
        IHtmlDocument document = await DocumentAsync(site.Factory, $"/dit/{culture}/dotnet/questions/");
        CatalogPage live = await LiveAsync(site.Factory, culture);

        Assert.True(live.Rows.Count >= 3);
        IElement[] rows = [.. document.QuerySelectorAll("ol.rows > li .row")];
        Assert.Equal(live.Rows.Count, rows.Length);
        Assert.Equal(
            live.Rows.Select(r => $"/dit/{culture}/dotnet/questions/{r.Slug}/").Order(StringComparer.Ordinal),
            rows.Select(r => r.QuerySelector("a.row__title")!.GetAttribute("href")!).Order(StringComparer.Ordinal));
        Assert.Empty(document.QuerySelectorAll(".pager"));
        Assert.Empty(document.QuerySelectorAll("a.filters__item"));
        Assert.Equal($"https://example.test/dit/{culture}/dotnet/questions/", document.QuerySelector("link[rel=canonical]")?.GetAttribute("href"));
        Assert.Null(document.QuerySelector("meta[name=robots]"));
    }

    [Fact]
    public async Task StaticCatalog_RowsCarryLevelTopicTypeTitleAndTags()
    {
        IHtmlDocument document = await DocumentAsync(site.Factory, "/dit/ru/dotnet/questions/");
        CatalogPage live = await LiveAsync(site.Factory, "ru");

        foreach (CatalogRow expected in live.Rows)
        {
            IElement row = RowOf(document, expected.Slug);
            Assert.Equal(LevelKeys.ToKey(expected.Level), row.GetAttribute("data-level"));
            Assert.Equal(expected.Topic.Value, row.GetAttribute("data-topic"));
            Assert.Equal(QuestionTypeKeys.ToKey(expected.Type), row.GetAttribute("data-type"));
            Assert.Equal(expected.Title.ToLowerInvariant(), row.GetAttribute("data-title"));
            Assert.Equal(string.Join(' ', expected.Tags), row.GetAttribute("data-tags"));
        }

        IElement closure = RowOf(document, "closure-for-loop");
        Assert.Equal("junior", closure.GetAttribute("data-level"));
        Assert.Equal("csharp", closure.GetAttribute("data-topic"));
        Assert.Equal("output", closure.GetAttribute("data-type"));
        Assert.Equal("closures lambda for-loop", closure.GetAttribute("data-tags"));
        // The visible title keeps its capitals, so the lowercase attribute is a separate value and not a copy.
        string title = closure.QuerySelector("a.row__title")!.TextContent;
        Assert.NotEqual(title, closure.GetAttribute("data-title"));
        Assert.Equal(title.ToLowerInvariant(), closure.GetAttribute("data-title"));
    }

    [Fact]
    public async Task StaticCatalog_FilterFormIsGetWithCheckboxesAndNoToken()
    {
        IHtmlDocument document = await DocumentAsync(site.Factory, "/dit/en/dotnet/questions/");
        CatalogPage live = await LiveAsync(site.Factory, "en");

        IElement form = Assert.Single(document.QuerySelectorAll("main form"));
        Assert.Equal("get", form.GetAttribute("method"));
        Assert.Equal("search", form.GetAttribute("role"));
        Assert.Equal("/dit/en/dotnet/questions/", form.GetAttribute("action"));
        IElement search = form.QuerySelector("input[name=q]")!;
        Assert.Equal("100", search.GetAttribute("maxlength"));
        Assert.Equal("search", search.GetAttribute("type"));

        Assert.Equal(LevelKeys.All.Select(LevelKeys.ToKey), Values(form, "level"));
        Assert.Equal(live.TopicCounts.Select(t => t.Key), Values(form, "topic"));
        Assert.Equal(QuestionTypeKeys.All.Select(QuestionTypeKeys.ToKey), Values(form, "type"));
        Assert.Equal(
            live.TopicCounts.Select(t => t.Title),
            form.QuerySelectorAll("input[name=topic]").Select(i => i.ParentElement!.TextContent.Trim()));
        Assert.Equal(["Level", "Topic", "Type"], form.QuerySelectorAll("fieldset > legend").Select(l => l.TextContent.Trim()));

        string[] known = form.GetAttribute("data-known-topics")!.Split(' ');
        Assert.All(Values(form, "topic"), t => Assert.Contains(t, known));
        Assert.Equal(known.Length, known.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("Apply filters", form.QuerySelector("button[type=submit]")!.TextContent.Trim());
        Assert.Empty(document.QuerySelectorAll("input[type=hidden]"));
        Assert.Empty(document.QuerySelectorAll("input[name=__RequestVerificationToken]"));
        Assert.Contains(document.Scripts, s => System.Text.RegularExpressions.Regex.IsMatch(
            s.Source ?? string.Empty, @"^/dit/js/catalog-filter(\.[a-z0-9]+)?\.js$", System.Text.RegularExpressions.RegexOptions.CultureInvariant));
        Assert.DoesNotContain(document.Scripts, s => string.IsNullOrEmpty(s.Source) && s.GetAttribute("type") != "application/ld+json"
            && s.TextContent.Contains("catalog-filter", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StaticCatalog_CountAndEmptyStateAreReadyForTheScript()
    {
        IHtmlDocument document = await DocumentAsync(site.Factory, "/dit/en/dotnet/questions/");
        CatalogPage live = await LiveAsync(site.Factory, "en");

        IElement found = document.QuerySelector("p.catalog__found")!;
        Assert.Equal("Found: {0}", found.GetAttribute("data-found-template"));
        Assert.Equal($"Found: {live.Total}", found.TextContent.Trim());
        IElement empty = document.QuerySelector("[data-catalog-empty]")!;
        Assert.Equal("No questions match these filters.", empty.TextContent.Trim());
        Assert.True(empty.HasAttribute("hidden"));
    }

    [Fact]
    public async Task ServerCatalog_IsUnchanged()
    {
        await using TrainerWebFactory server = await TrainerWebFactory.CreateAsync(postgres);
        await server.WaitUntilReadyAsync();

        IHtmlDocument document = await DocumentAsync(server, "/en/dotnet/questions");
        CatalogPage live = await LiveAsync(server, "en");

        Assert.NotEmpty(document.QuerySelectorAll("nav.filters__panel a.filters__item[href^=\"/en/dotnet/questions?\"]"));
        Assert.Empty(document.QuerySelectorAll("input[type=checkbox]"));
        Assert.Empty(document.QuerySelectorAll("[data-level], [data-topic], [data-type], [data-title], [data-tags], [data-found-template], [data-catalog-empty]"));
        Assert.DoesNotContain(document.Scripts, s => (s.Source ?? string.Empty).Contains("catalog-filter", StringComparison.Ordinal));
        Assert.Equal(Math.Min(live.Rows.Count, CatalogQuery.PageSize), document.QuerySelectorAll("ol.rows > li").Length);
        Assert.Equal("/en/dotnet/questions", document.QuerySelector("form[role=search]")!.GetAttribute("action"));
    }
}
