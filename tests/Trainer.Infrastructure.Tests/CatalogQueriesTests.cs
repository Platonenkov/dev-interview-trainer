using Trainer.Application.Catalog;
using Microsoft.EntityFrameworkCore;
using Trainer.Application.Home;
using Trainer.Application.Seo;
using Trainer.Application.Topics;
using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;
using Trainer.Infrastructure.Persistence;
using Trainer.Infrastructure.Persistence.Entities;
using Trainer.Infrastructure.Queries;
using Trainer.TestSupport;

namespace Trainer.Infrastructure.Tests;

public sealed class CatalogQueriesTests(PostgresFixture postgres)
{
    private static readonly CultureKey Ru = new("ru");
    private static readonly CultureKey En = new("en");
    private static readonly StackKey Dotnet = new("dotnet");

    private static CatalogQuery Query(
        CultureKey? culture = null,
        Level[]? levels = null,
        string[]? topics = null,
        QuestionType[]? types = null,
        int page = 1) =>
        new(culture ?? En, Dotnet, levels ?? [], [.. (topics ?? []).Select(t => new TopicKey(t))], types ?? [], page);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Topics a (two questions) and b (one) plus c (retired question only), and an empty topic d.
    /// <c>q-gone</c> is retired, <c>q-en-only</c> has no ru locale.
    /// </summary>
    private async Task<TrainerDbContext> SeedAsync()
    {
        string connectionString = await postgres.CreateDatabaseAsync();
        TrainerDbContext db = PostgresFixture.CreateContext(connectionString);

        db.Stacks.Add(new StackEntity { Key = "dotnet" });
        foreach (string topic in new[] { "a", "b", "c", "d" })
        {
            db.Topics.Add(new TopicEntity { StackKey = "dotnet", Key = topic });
            db.TopicLocales.Add(new TopicLocaleEntity { StackKey = "dotnet", TopicKey = topic, Culture = "en", Title = $"Topic {topic.ToUpperInvariant()}", Description = "" });
            db.TopicLocales.Add(new TopicLocaleEntity { StackKey = "dotnet", TopicKey = topic, Culture = "ru", Title = $"Тема {topic.ToUpperInvariant()}", Description = "" });
        }

        AddQuestion(db, "01KBZ3Q8N9V2K3M4P5R6S7T801", "junior-open", "a", Level.Junior, QuestionType.Open, ru: "Б junior", en: "B junior");
        AddQuestion(db, "01KBZ3Q8N9V2K3M4P5R6S7T802", "middle-single", "a", Level.Middle, QuestionType.Single, ru: "Middle А", en: "Middle A");
        AddQuestion(db, "01KBZ3Q8N9V2K3M4P5R6S7T803", "middle-output", "b", Level.Middle, QuestionType.Output, ru: "Middle Б", en: "Middle B");
        AddQuestion(db, "01KBZ3Q8N9V2K3M4P5R6S7T804", "q-gone", "c", Level.Senior, QuestionType.Open, ru: "Снят", en: "Gone", retired: true);
        AddQuestion(db, "01KBZ3Q8N9V2K3M4P5R6S7T805", "q-en-only", "b", Level.Senior, QuestionType.Open, ru: null, en: "English only");

        await db.SaveChangesAsync(Ct);
        return db;
    }

    private static void AddQuestion(
        TrainerDbContext db, string id, string slug, string topic, Level level, QuestionType type, string? ru, string en, bool retired = false)
    {
        db.Questions.Add(new QuestionEntity
        {
            Id = id,
            StackKey = "dotnet",
            TopicKey = topic,
            Slug = slug,
            Level = level,
            Type = type,
            Grading = GradingMode.Self,
            Tags = ["tag-" + slug],
            Verified = new DateOnly(2026, 11, 11),
            RetiredAt = retired ? new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero) : null,
        });

        if (ru is not null)
        {
            db.QuestionLocales.Add(Locale(id, "ru", ru));
        }

        db.QuestionLocales.Add(Locale(id, "en", en));
    }

    private static QuestionLocaleEntity Locale(string id, string culture, string title) => new()
    {
        QuestionId = id,
        Culture = culture,
        Title = title,
        PromptHtml = "",
        AnswerHtml = "",
        ExplanationHtml = "",
        Options = "[]",
        Links = "[]",
    };

    private static int CountOf(IReadOnlyList<FacetCount> facets, string key) => facets.Single(f => f.Key == key).Count;

    [Fact]
    public async Task CatalogQueries_FiltersByLevelTopicType()
    {
        await using TrainerDbContext db = await SeedAsync();
        CatalogQueries queries = new(db);

        CatalogPage byLevel = await queries.GetAsync(Query(levels: [Level.Middle]), Ct);
        CatalogPage byTopic = await queries.GetAsync(Query(topics: ["b"]), Ct);
        CatalogPage byType = await queries.GetAsync(Query(types: [QuestionType.Open, QuestionType.Output]), Ct);
        CatalogPage combined = await queries.GetAsync(Query(levels: [Level.Middle], topics: ["a"], types: [QuestionType.Single]), Ct);

        Assert.Equal(["middle-output", "middle-single"], byLevel.Rows.Select(r => r.Slug).Order(StringComparer.Ordinal));
        Assert.Equal(["middle-output", "q-en-only"], byTopic.Rows.Select(r => r.Slug).Order(StringComparer.Ordinal));
        Assert.Equal(["junior-open", "middle-output", "q-en-only"], byType.Rows.Select(r => r.Slug).Order(StringComparer.Ordinal));
        CatalogRow only = Assert.Single(combined.Rows);
        Assert.Equal("middle-single", only.Slug);
        Assert.Equal("Topic A", only.TopicTitle);
        Assert.Equal(["tag-middle-single"], only.Tags);
    }

    [Fact]
    public async Task CatalogQueries_OrdersByLevelThenTitle()
    {
        await using TrainerDbContext db = await SeedAsync();

        CatalogPage page = await new CatalogQueries(db).GetAsync(Query(), Ct);

        Assert.Equal(["B junior", "Middle A", "Middle B", "English only"], page.Rows.Select(r => r.Title).ToArray());
    }

    [Fact]
    public async Task CatalogQueries_FacetCountsIgnoreOwnDimension()
    {
        await using TrainerDbContext db = await SeedAsync();

        CatalogPage page = await new CatalogQueries(db).GetAsync(Query(levels: [Level.Middle], topics: ["a"]), Ct);

        Assert.Equal(["middle-single"], page.Rows.Select(r => r.Slug));
        Assert.Equal(1, page.Total);
        // Levels ignore the level filter and keep the topic filter: topic a holds a junior and a middle question.
        Assert.Equal(1, CountOf(page.LevelCounts, "junior"));
        Assert.Equal(1, CountOf(page.LevelCounts, "middle"));
        Assert.Equal(0, CountOf(page.LevelCounts, "senior"));
        Assert.Equal(5, page.LevelCounts.Count);
        // Topics ignore the topic filter and keep the level filter.
        Assert.Equal(1, CountOf(page.TopicCounts, "a"));
        Assert.Equal(1, CountOf(page.TopicCounts, "b"));
        // Types keep both filters.
        Assert.Equal(1, CountOf(page.TypeCounts, "single"));
        Assert.Equal(0, CountOf(page.TypeCounts, "output"));
        Assert.Equal(6, page.TypeCounts.Count);
    }

    [Fact]
    public async Task CatalogQueries_ExcludesRetiredAndMissingLocale()
    {
        await using TrainerDbContext db = await SeedAsync();
        CatalogQueries queries = new(db);

        CatalogPage ru = await queries.GetAsync(Query(Ru), Ct);
        CatalogPage en = await queries.GetAsync(Query(En), Ct);

        Assert.Equal(3, ru.Total);
        Assert.DoesNotContain(ru.Rows, r => r.Slug is "q-gone" or "q-en-only");
        Assert.Equal(4, en.Total);
        Assert.DoesNotContain(en.Rows, r => r.Slug == "q-gone");
        // Topic c has only a retired question and d has none: both are omitted; b counts the en-only question in en only.
        Assert.Equal(["a", "b"], ru.TopicCounts.Select(t => t.Key).ToArray());
        Assert.Equal(1, CountOf(ru.TopicCounts, "b"));
        Assert.Equal(2, CountOf(en.TopicCounts, "b"));
        Assert.Equal("Тема A", ru.TopicCounts[0].Title);
    }

    [Fact]
    public async Task CatalogQueries_KeepsSelectedTopicAtZero()
    {
        await using TrainerDbContext db = await SeedAsync();

        CatalogPage page = await new CatalogQueries(db).GetAsync(Query(Ru, topics: ["d"]), Ct);

        Assert.Empty(page.Rows);
        Assert.Equal(1, page.PageCount);
        Assert.Equal(0, CountOf(page.TopicCounts, "d"));
    }

    [Fact]
    public async Task CatalogQueries_Pages()
    {
        string connectionString = await postgres.CreateDatabaseAsync();
        await using TrainerDbContext db = PostgresFixture.CreateContext(connectionString);
        db.Stacks.Add(new StackEntity { Key = "dotnet" });
        db.Topics.Add(new TopicEntity { StackKey = "dotnet", Key = "a" });
        for (int i = 0; i < 27; i++)
        {
            AddQuestion(db, $"01KBZ3Q8N9V2K3M4P5R6S7T9{i:00}", $"q-{i:00}", "a", Level.Middle, QuestionType.Open, ru: null, en: $"Question {i:00}");
        }

        await db.SaveChangesAsync(Ct);
        CatalogQueries queries = new(db);

        CatalogPage first = await queries.GetAsync(Query(page: 1), Ct);
        CatalogPage second = await queries.GetAsync(Query(page: 2), Ct);
        CatalogPage beyond = await queries.GetAsync(Query(page: 9), Ct);

        Assert.Equal(27, first.Total);
        Assert.Equal(2, first.PageCount);
        Assert.Equal(CatalogQuery.PageSize, first.Rows.Count);
        Assert.Equal("Question 00", first.Rows[0].Title);
        Assert.Equal(["Question 25", "Question 26"], second.Rows.Select(r => r.Title).ToArray());
        Assert.Equal(2, beyond.Page);
        Assert.Equal(2, beyond.Rows.Count);
        // No topic locale exists here, so the key stands in for the title.
        Assert.Equal("a", first.Rows[0].TopicTitle);
    }

    [Fact]
    public async Task RetiredTopic_IsHiddenFromCatalogHomeTaxonomyTopicPageAndSitemap()
    {
        await using TrainerDbContext db = await SeedAsync();
        db.StackLocales.Add(new StackLocaleEntity { StackKey = "dotnet", Culture = "en", Title = ".NET", Description = "" });
        await db.SaveChangesAsync(Ct);
        await db.Topics.Where(t => t.Key == "a").ExecuteUpdateAsync(s => s.SetProperty(t => t.RetiredAt, DateTimeOffset.UtcNow), Ct);

        CatalogPage catalog = await new CatalogQueries(db).GetAsync(Query(), Ct);
        HomeStats home = await new HomeQueries(db).GetStatsAsync(En, Dotnet, Ct);
        IReadOnlyDictionary<StackKey, IReadOnlyList<TopicKey>> taxonomy = await new StackQueries(db).GetTaxonomyAsync(Ct);
        TopicPageModel? topicPage = await new TopicQueries(db).GetAsync(En, Dotnet, new TopicKey("a"), Ct);
        IReadOnlyList<SitemapEntry> sitemap = await new SitemapQueries(db).GetAsync(En, Ct);

        Assert.Equal(["Middle B", "English only"], catalog.Rows.Select(r => r.Title).ToArray());
        Assert.DoesNotContain(catalog.TopicCounts, t => t.Key == "a");
        Assert.DoesNotContain(home.Topics, t => t.Key == "a");
        Assert.Equal(2, home.QuestionCount);
        Assert.DoesNotContain(new TopicKey("a"), taxonomy[Dotnet]);
        Assert.Contains(new TopicKey("b"), taxonomy[Dotnet]);
        Assert.Null(topicPage);
        Assert.DoesNotContain(sitemap, e => e.Path.EndsWith("/topics/a", StringComparison.Ordinal));
        Assert.Contains(sitemap, e => e.Path.EndsWith("/topics/b", StringComparison.Ordinal));
        Assert.DoesNotContain(sitemap, e => e.Path.EndsWith("/questions/junior-open", StringComparison.Ordinal));
        Assert.DoesNotContain(sitemap, e => e.Path.EndsWith("/questions/middle-single", StringComparison.Ordinal));
        Assert.Contains(sitemap, e => e.Path.EndsWith("/questions/middle-output", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HomeQueries_CountsOnlyLiveQuestionsOfTheCulture()
    {
        await using TrainerDbContext db = await SeedAsync();
        HomeQueries queries = new(db);

        HomeStats ru = await queries.GetStatsAsync(Ru, Dotnet, Ct);
        HomeStats en = await queries.GetStatsAsync(En, Dotnet, Ct);

        Assert.Equal(3, ru.QuestionCount);
        Assert.Equal(2, ru.TopicCount);
        Assert.Equal(3, ru.TypeCount);
        Assert.Equal(["a", "b"], ru.Topics.Select(t => t.Key).ToArray());
        Assert.Equal(4, en.QuestionCount);
        Assert.Equal(3, en.TypeCount);
    }
}
