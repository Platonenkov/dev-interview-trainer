using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Trainer.Application.Seo;
using Trainer.Application.Sessions;
using Trainer.Application.StaticSite;
using Trainer.Contracts;
using Trainer.Domain.Grading;
using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;
using Trainer.Domain.Sessions;
using Trainer.Infrastructure.Feedback;
using Trainer.Infrastructure.Persistence;
using Trainer.Infrastructure.Persistence.Entities;
using Trainer.Infrastructure.Queries;
using Trainer.Infrastructure.Sessions;
using Trainer.Infrastructure.StaticSite;
using Trainer.TestSupport;

namespace Trainer.Infrastructure.Tests;

public sealed class StaticSiteQueriesTests(PostgresFixture postgres)
{
    private static readonly CultureKey En = new("en");
    private static readonly CultureKey Ru = new("ru");
    private static readonly StackKey Dotnet = new("dotnet");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StaticSiteQueries CreateQueries(TrainerDbContext db) => new(db, new SitemapQueries(db));

    private static string Json(QuestionDto question) => JsonSerializer.Serialize(question, TrainerJsonContext.Default.QuestionDto);

    private static async Task AddQuestionAsync(LearningDatabase data, string id, string slug, bool english, bool russian)
    {
        data.Db.Questions.Add(new QuestionEntity
        {
            Id = id,
            StackKey = "dotnet",
            TopicKey = "async",
            Slug = slug,
            Level = Level.Middle,
            Type = QuestionType.Open,
            Grading = GradingMode.Self,
            Verified = new DateOnly(2026, 11, 11),
        });
        foreach ((bool present, string culture) in new[] { (english, "en"), (russian, "ru") })
        {
            if (present)
            {
                data.Db.QuestionLocales.Add(new QuestionLocaleEntity
                {
                    QuestionId = id,
                    Culture = culture,
                    Title = "Extra " + id,
                    PromptHtml = "<p>p</p>",
                    AnswerHtml = "<p>a</p>",
                    ExplanationHtml = "<p>e</p>",
                    Options = "[]",
                    Links = "[]",
                });
            }
        }

        await data.Db.SaveChangesAsync(Ct);
        data.Db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task LiveQuestions_EqualTheSessionDtoForEachQuestion()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        await using TrainerDbContext db = data.NewContext();
        StaticSiteQueries queries = CreateQueries(db);
        SessionQueries sessions = new(db, data.Clock, new MockComposer(new Random(5)), new QuestionFeedbackQueries(db));

        foreach (CultureKey culture in CultureKey.Supported)
        {
            IReadOnlyList<QuestionDto> live = await queries.GetLiveQuestionsAsync(culture, Dotnet, Ct);

            Assert.Equal(data.Questions.Count, live.Count);
            foreach (QuestionDto question in live)
            {
                PracticeCriteria criteria = new(culture, Dotnet, [], [], [], PracticeStatus.All, 1, new QuestionId(question.Id), []);
                SessionDto session = await sessions.GetPracticeAsync(criteria, null, Ct);

                Assert.Equal(question.Id, session.Questions[0].Id);
                Assert.Equal(Json(session.Questions[0]), Json(question));
            }
        }
    }

    [Fact]
    public async Task LiveQuestions_AreOrderedByIdOrdinal()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        // Lowercase and uppercase ids would swap places under a case-insensitive collation.
        await AddQuestionAsync(data, "01KBZ3Q8N9V2K3M4P5R6S7T8Za", "extra-lower", english: true, russian: true);
        await AddQuestionAsync(data, "01KBZ3Q8N9V2K3M4P5R6S7T8ZB", "extra-upper", english: true, russian: true);
        await using TrainerDbContext db = data.NewContext();

        IReadOnlyList<QuestionDto> live = await CreateQueries(db).GetLiveQuestionsAsync(En, Dotnet, Ct);

        string[] ids = [.. live.Select(q => q.Id)];
        Assert.Equal(ids.Order(StringComparer.Ordinal), ids);
        Assert.Equal(data.Questions.Count + 2, ids.Length);
    }

    [Fact]
    public async Task LiveQuestions_LeaveOutRetiredAndMissingLocale()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        await data.RetireQuestionAsync(data.Questions[0], Ct);
        const string russianOnly = "01KBZ3Q8N9V2K3M4P5R6S7T8ZZ";
        await AddQuestionAsync(data, russianOnly, "russian-only", english: false, russian: true);
        await using TrainerDbContext db = data.NewContext();
        StaticSiteQueries queries = CreateQueries(db);

        string[] english = [.. (await queries.GetLiveQuestionsAsync(En, Dotnet, Ct)).Select(q => q.Id)];
        string[] russian = [.. (await queries.GetLiveQuestionsAsync(Ru, Dotnet, Ct)).Select(q => q.Id)];

        Assert.DoesNotContain(data.Questions[0].Value, english);
        Assert.DoesNotContain(data.Questions[0].Value, russian);
        Assert.DoesNotContain(russianOnly, english);
        Assert.Contains(russianOnly, russian);
        Assert.Equal(data.Questions.Count - 1, english.Length);
        Assert.Equal(data.Questions.Count, russian.Length);
    }

    [Fact]
    public async Task LiveQuestions_OfAnUnknownStackAreEmpty()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        await using TrainerDbContext db = data.NewContext();

        Assert.Empty(await CreateQueries(db).GetLiveQuestionsAsync(En, new StackKey("nope"), Ct));
    }

    [Fact]
    public async Task PagePaths_CoverSitemapRetiredQuestionsAndRedirectSources()
    {
        await using LearningDatabase data = await LearningDatabase.CreateAsync(postgres, Ct);
        QuestionId retired = data.Questions[0];
        string retiredSlug = await data.Db.Questions.Where(q => q.Id == retired.Value).Select(q => q.Slug).SingleAsync(Ct);
        await data.RetireQuestionAsync(retired, Ct);
        // A retired question that was also renamed answers a 301, so it is a redirect source and not a page.
        QuestionId renamed = data.Questions[1];
        string renamedSlug = await data.Db.Questions.Where(q => q.Id == renamed.Value).Select(q => q.Slug).SingleAsync(Ct);
        await data.RetireQuestionAsync(renamed, Ct);
        data.Db.Redirects.Add(new RedirectEntity { StackKey = "dotnet", FromSlug = renamedSlug, ToSlug = retiredSlug });
        data.Db.Redirects.Add(new RedirectEntity { StackKey = "dotnet", FromSlug = "old-name", ToSlug = retiredSlug });
        await data.Db.SaveChangesAsync(Ct);
        await using TrainerDbContext db = data.NewContext();

        StaticPagePaths paths = await CreateQueries(db).GetPagePathsAsync(Ct);

        foreach (CultureKey culture in CultureKey.Supported)
        {
            IReadOnlyList<SitemapEntry> sitemap = await new SitemapQueries(db).GetAsync(culture, Ct);
            Assert.NotEmpty(sitemap);
            foreach (SitemapEntry entry in sitemap)
            {
                Assert.Contains(entry.Path, paths.Pages);
            }

            Assert.Contains($"/{culture.Value}/dotnet/questions/{retiredSlug}", paths.Pages);
            Assert.Contains($"/{culture.Value}/dotnet/questions/old-name", paths.Redirects);
            Assert.Contains($"/{culture.Value}/dotnet/questions/{renamedSlug}", paths.Redirects);
            Assert.DoesNotContain($"/{culture.Value}/dotnet/questions/{renamedSlug}", paths.Pages);
        }

        Assert.Equal(4, paths.Redirects.Count);
        Assert.Empty(paths.Pages.Intersect(paths.Redirects, StringComparer.Ordinal));
        Assert.Equal(paths.Pages.Order(StringComparer.Ordinal), paths.Pages);
        Assert.Equal(paths.Redirects.Order(StringComparer.Ordinal), paths.Redirects);
        Assert.Equal(paths.Pages.Count, paths.Pages.Distinct(StringComparer.Ordinal).Count());
    }
}
