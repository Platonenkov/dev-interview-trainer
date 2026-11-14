using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Trainer.Application.Seo;
using Trainer.Domain.Keys;
using Trainer.Infrastructure.Queries;
using Microsoft.Extensions.Logging.Abstractions;
using Trainer.Application.Abstractions;
using Trainer.Application.Content;
using Trainer.Content.Reading;
using Trainer.Content.Rendering;
using Trainer.Content.Validation;
using Trainer.Infrastructure.Content;
using Trainer.Infrastructure.Persistence;
using Trainer.Infrastructure.Persistence.Entities;
using Trainer.TestSupport;

[assembly: AssemblyFixture(typeof(PostgresFixture))]

namespace Trainer.Infrastructure.Tests;

public sealed class ContentImportServiceTests(PostgresFixture postgres)
{
    private const string ClosureFile = "dotnet/csharp/closure-for-loop.yaml";
    private const string ClosureId = "01KBZ3Q8N9V2K3M4P5R6S7T8WA";

    // The fixtures were verified on 2026-11-11; a fixed clock keeps the verified-in-future rule deterministic.
    private static readonly FixedTimeProvider Clock = new(new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero));

    private static ContentImportService CreateService(
        TrainerDbContext db, IContentCacheInvalidator invalidator, IContentReader? reader = null, ILogger<ContentImportService>? logger = null)
    {
        ContentRenderer renderer = new();
        return new ContentImportService(
            db, reader ?? new ContentReader(), new ContentValidator(renderer, Clock), renderer, invalidator, Clock, logger ?? NullLogger<ContentImportService>.Instance);
    }

    private static ContentWorkspace WithClosureV2(ContentWorkspace source)
    {
        ContentWorkspace copy = source.Clone();
        copy.Edit(ClosureFile, "What does the code print when lambdas", "What does the code print (v2) when lambdas");
        return copy;
    }

    private static ContentWorkspace WithoutCsharpTopic(ContentWorkspace source)
    {
        ContentWorkspace copy = source.Clone();
        copy.Delete(ClosureFile);
        string topics = copy.Read("dotnet/topics.yaml");
        int start = topics.IndexOf("- key: csharp", StringComparison.Ordinal);
        int next = topics.IndexOf("- key: async", StringComparison.Ordinal);
        copy.Write("dotnet/topics.yaml", topics.Remove(start, next - start));
        return copy;
    }

    private static ContentWorkspace WithRenamedClosure(ContentWorkspace source, bool addRedirect)
    {
        ContentWorkspace copy = source.Clone();
        copy.Edit(ClosureFile, "slug: closure-for-loop", "slug: closure-in-for-loop");
        copy.Move(ClosureFile, "dotnet/csharp/closure-in-for-loop.yaml");
        if (addRedirect)
        {
            copy.Write("dotnet/redirects.yaml", "- from: csharp/closure-for-loop\n  to: csharp/closure-in-for-loop\n");
        }

        return copy;
    }

    private async Task<(TrainerDbContext Db, ContentImportService Service, FakeInvalidator Invalidator)> NewScopeAsync()
    {
        string connectionString = await postgres.CreateDatabaseAsync();
        TrainerDbContext db = PostgresFixture.CreateContext(connectionString);
        FakeInvalidator invalidator = new();
        return (db, CreateService(db, invalidator), invalidator);
    }

    [Fact]
    public async Task Import_ValidContent_WritesQuestionsAndLocales()
    {
        (TrainerDbContext db, ContentImportService service, _) = await NewScopeAsync();
        await using (db)
        {
            using ContentWorkspace content = ContentWorkspace.CopyOfValid();

            ContentImportResult result = await service.ImportAsync(content.Root, TestContext.Current.CancellationToken);

            Assert.Equal(ContentImportStatus.Imported, result.Status);
            Assert.Equal(6, result.Imported);
            Assert.Empty(result.Issues);
            Assert.Equal(6, await db.Questions.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(12, await db.QuestionLocales.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(3, await db.Topics.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(2, await db.StackLocales.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(6, await db.TopicLocales.CountAsync(TestContext.Current.CancellationToken));
            QuestionEntity closure = await db.Questions.SingleAsync(q => q.Id == ClosureId, TestContext.Current.CancellationToken);
            Assert.Equal(new DateOnly(2026, 11, 11), closure.Verified);
            Assert.Equal("closure-for-loop", closure.Slug);
            Assert.Contains("<span", closure.Payload, StringComparison.Ordinal);
            List<string> prompts = await db.QuestionLocales.Select(l => l.PromptHtml).ToListAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(prompts, p => p.Contains("<script", StringComparison.OrdinalIgnoreCase));
            Assert.All(prompts, p => Assert.False(string.IsNullOrWhiteSpace(p)));
            ContentImportEntity active = await db.ContentImports.SingleAsync(i => i.IsActive, TestContext.Current.CancellationToken);
            Assert.Equal(result.Hash, active.Hash);
            Assert.Equal(6, active.QuestionCount);
        }
    }

    [Fact]
    public async Task Import_SameContentTwice_SecondIsUnchanged()
    {
        (TrainerDbContext db, ContentImportService service, _) = await NewScopeAsync();
        await using (db)
        {
            using ContentWorkspace content = ContentWorkspace.CopyOfValid();

            ContentImportResult first = await service.ImportAsync(content.Root, TestContext.Current.CancellationToken);
            ContentImportResult second = await service.ImportAsync(content.Root, TestContext.Current.CancellationToken);

            Assert.Equal(ContentImportStatus.Imported, first.Status);
            Assert.Equal(ContentImportStatus.Unchanged, second.Status);
            Assert.Equal(first.Hash, second.Hash);
            Assert.Equal(1, await db.ContentImports.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, await db.ContentImports.CountAsync(i => i.IsActive, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Import_RollbackAtoBtoA_Succeeds()
    {
        (TrainerDbContext db, ContentImportService service, _) = await NewScopeAsync();
        await using (db)
        {
            using ContentWorkspace a = ContentWorkspace.CopyOfValid();
            using ContentWorkspace b = WithClosureV2(a);

            ContentImportResult first = await service.ImportAsync(a.Root, TestContext.Current.CancellationToken);
            ContentImportResult second = await service.ImportAsync(b.Root, TestContext.Current.CancellationToken);
            ContentImportResult third = await service.ImportAsync(a.Root, TestContext.Current.CancellationToken);

            Assert.Equal(ContentImportStatus.Imported, second.Status);
            Assert.Equal(ContentImportStatus.Imported, third.Status);
            Assert.Equal(first.Hash, third.Hash);
            Assert.NotEqual(first.Hash, second.Hash);
            Assert.Equal(3, await db.ContentImports.CountAsync(TestContext.Current.CancellationToken));
            ContentImportEntity active = await db.ContentImports.SingleAsync(i => i.IsActive, TestContext.Current.CancellationToken);
            Assert.Equal(first.Hash, active.Hash);
            QuestionLocaleEntity locale = await db.QuestionLocales.SingleAsync(l => l.QuestionId == ClosureId && l.Culture == "en", TestContext.Current.CancellationToken);
            Assert.DoesNotContain("(v2)", locale.Title, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Import_RenderVersionChange_Reimports()
    {
        (TrainerDbContext db, ContentImportService service, _) = await NewScopeAsync();
        await using (db)
        {
            using ContentWorkspace content = ContentWorkspace.CopyOfValid();
            await service.ImportAsync(content.Root, TestContext.Current.CancellationToken);
            await db.Database.ExecuteSqlAsync($"UPDATE content_imports SET render_version = 0 WHERE is_active", TestContext.Current.CancellationToken);

            ContentImportResult result = await service.ImportAsync(content.Root, TestContext.Current.CancellationToken);

            Assert.Equal(ContentImportStatus.Imported, result.Status);
            Assert.Equal(2, await db.ContentImports.CountAsync(TestContext.Current.CancellationToken));
            ContentImportEntity active = await db.ContentImports.AsNoTracking().SingleAsync(i => i.IsActive, TestContext.Current.CancellationToken);
            Assert.Equal(Trainer.Content.Schema.ContentSchema.RenderVersion, active.RenderVersion);
        }
    }

    [Fact]
    public async Task Import_RenderVersion3_ReimportsAndStoresCodeLines()
    {
        (TrainerDbContext db, ContentImportService service, _) = await NewScopeAsync();
        await using (db)
        {
            using ContentWorkspace content = ContentWorkspace.CopyOfValid();
            await service.ImportAsync(content.Root, TestContext.Current.CancellationToken);
            await db.Database.ExecuteSqlAsync($"UPDATE content_imports SET render_version = 2 WHERE is_active", TestContext.Current.CancellationToken);
            await db.Database.ExecuteSqlAsync($"UPDATE questions SET payload = payload - 'codeLinesHtml'", TestContext.Current.CancellationToken);

            ContentImportResult result = await service.ImportAsync(content.Root, TestContext.Current.CancellationToken);

            Assert.Equal(ContentImportStatus.Imported, result.Status);
            Assert.Equal(3, Trainer.Content.Schema.ContentSchema.RenderVersion);
            QuestionEntity closure = await db.Questions.AsNoTracking().SingleAsync(q => q.Id == ClosureId, TestContext.Current.CancellationToken);
            using System.Text.Json.JsonDocument payload = System.Text.Json.JsonDocument.Parse(closure.Payload);
            string[] lines = [.. payload.RootElement.GetProperty("codeLinesHtml").EnumerateArray().Select(e => e.GetString()!)];
            string codeHtml = payload.RootElement.GetProperty("codeHtml").GetString()!;
            Assert.NotEmpty(lines);
            Assert.Equal($"<pre class=\"code\"><code>{string.Join('\n', lines)}</code></pre>", codeHtml);
        }
    }

    [Fact]
    public async Task Import_RollbackOverSlugRename_Succeeds()
    {
        (TrainerDbContext db, ContentImportService service, _) = await NewScopeAsync();
        await using (db)
        {
            using ContentWorkspace a = ContentWorkspace.CopyOfValid();
            using ContentWorkspace b = WithRenamedClosure(a, addRedirect: true);

            await service.ImportAsync(a.Root, TestContext.Current.CancellationToken);
            ContentImportResult renamed = await service.ImportAsync(b.Root, TestContext.Current.CancellationToken);
            Assert.Equal(ContentImportStatus.Imported, renamed.Status);
            Assert.Equal("closure-in-for-loop", (await db.Questions.AsNoTracking().SingleAsync(q => q.Id == ClosureId, TestContext.Current.CancellationToken)).Slug);
            RedirectEntity redirect = await db.Redirects.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(("closure-for-loop", "closure-in-for-loop"), (redirect.FromSlug, redirect.ToSlug));

            ContentImportResult back = await service.ImportAsync(a.Root, TestContext.Current.CancellationToken);

            Assert.Equal(ContentImportStatus.Imported, back.Status);
            Assert.Equal("closure-for-loop", (await db.Questions.AsNoTracking().SingleAsync(q => q.Id == ClosureId, TestContext.Current.CancellationToken)).Slug);
            Assert.Empty(await db.Redirects.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Import_InvalidContent_IsRejectedAndWritesNothing()
    {
        (TrainerDbContext db, ContentImportService service, _) = await NewScopeAsync();
        await using (db)
        {
            using ContentWorkspace good = ContentWorkspace.CopyOfValid();
            using ContentWorkspace bad = good.Clone();
            bad.Edit(ClosureFile, "level: junior", "level: wizard");
            ContentImportResult first = await service.ImportAsync(good.Root, TestContext.Current.CancellationToken);

            ContentImportResult rejected = await service.ImportAsync(bad.Root, TestContext.Current.CancellationToken);

            Assert.Equal(ContentImportStatus.Rejected, rejected.Status);
            Assert.Contains(rejected.Issues, i => i.Code == IssueCodes.LevelInvalid && i.Path == ClosureFile);
            Assert.Equal(0, rejected.Imported);
            Assert.Equal(1, await db.ContentImports.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(first.Hash, (await db.ContentImports.SingleAsync(i => i.IsActive, TestContext.Current.CancellationToken)).Hash);
            Assert.Equal(6, await db.Questions.CountAsync(q => q.RetiredAt == null, TestContext.Current.CancellationToken));
            Assert.Equal(Trainer.Domain.Levels.Level.Junior, (await db.Questions.AsNoTracking().SingleAsync(q => q.Id == ClosureId, TestContext.Current.CancellationToken)).Level);
        }
    }

    [Fact]
    public async Task Import_RemovedFile_RetiresQuestion()
    {
        (TrainerDbContext db, ContentImportService service, _) = await NewScopeAsync();
        await using (db)
        {
            using ContentWorkspace a = ContentWorkspace.CopyOfValid();
            using ContentWorkspace b = a.Clone();
            b.Delete(ClosureFile);
            await service.ImportAsync(a.Root, TestContext.Current.CancellationToken);

            ContentImportResult result = await service.ImportAsync(b.Root, TestContext.Current.CancellationToken);

            Assert.Equal(ContentImportStatus.Imported, result.Status);
            Assert.Equal(5, result.Imported);
            Assert.Equal(1, result.Retired);
            QuestionEntity retired = await db.Questions.AsNoTracking().SingleAsync(q => q.Id == ClosureId, TestContext.Current.CancellationToken);
            Assert.NotNull(retired.RetiredAt);
            Assert.Equal(2, await db.QuestionLocales.CountAsync(l => l.QuestionId == ClosureId, TestContext.Current.CancellationToken));
            Assert.Equal(5, await db.Questions.CountAsync(q => q.RetiredAt == null, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Import_VanishedTopic_IsRetiredNotDeleted_AndRollbackRestoresIt()
    {
        (TrainerDbContext db, ContentImportService service, _) = await NewScopeAsync();
        await using (db)
        {
            using ContentWorkspace a = ContentWorkspace.CopyOfValid();
            using ContentWorkspace b = WithoutCsharpTopic(a);
            await service.ImportAsync(a.Root, TestContext.Current.CancellationToken);

            ContentImportResult second = await service.ImportAsync(b.Root, TestContext.Current.CancellationToken);

            Assert.Equal(ContentImportStatus.Imported, second.Status);
            Assert.Equal(1, second.Retired);
            Assert.Equal(3, await db.Topics.CountAsync(TestContext.Current.CancellationToken));
            TopicEntity retired = await db.Topics.AsNoTracking().SingleAsync(t => t.Key == "csharp", TestContext.Current.CancellationToken);
            Assert.NotNull(retired.RetiredAt);
            Assert.Equal(2, await db.TopicLocales.CountAsync(l => l.TopicKey == "csharp", TestContext.Current.CancellationToken));
            Assert.Equal(2, await db.Topics.CountAsync(t => t.RetiredAt == null, TestContext.Current.CancellationToken));

            ContentImportResult third = await service.ImportAsync(a.Root, TestContext.Current.CancellationToken);

            Assert.Equal(ContentImportStatus.Imported, third.Status);
            Assert.Equal(3, await db.Topics.CountAsync(t => t.RetiredAt == null, TestContext.Current.CancellationToken));
            Assert.Equal(6, await db.Questions.CountAsync(q => q.RetiredAt == null, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Import_VanishedStack_IsRetiredNotDeleted_AndRollbackRestoresIt()
    {
        (TrainerDbContext db, ContentImportService service, _) = await NewScopeAsync();
        await using (db)
        {
            using ContentWorkspace b = ContentWorkspace.CopyOfValid();
            using ContentWorkspace a = WithLegacyStack(b);
            await service.ImportAsync(a.Root, TestContext.Current.CancellationToken);
            Assert.Equal(2, await db.Stacks.CountAsync(s => s.RetiredAt == null, TestContext.Current.CancellationToken));

            ContentImportResult second = await service.ImportAsync(b.Root, TestContext.Current.CancellationToken);

            Assert.Equal(ContentImportStatus.Imported, second.Status);
            Assert.Equal(2, await db.Stacks.CountAsync(TestContext.Current.CancellationToken));
            StackEntity retired = await db.Stacks.AsNoTracking().SingleAsync(s => s.Key == "legacy", TestContext.Current.CancellationToken);
            Assert.NotNull(retired.RetiredAt);
            Assert.Equal(2, await db.StackLocales.CountAsync(l => l.StackKey == "legacy", TestContext.Current.CancellationToken));
            IReadOnlyDictionary<StackKey, IReadOnlyList<TopicKey>> taxonomy = await new StackQueries(db).GetTaxonomyAsync(TestContext.Current.CancellationToken);
            Assert.Equal([new StackKey("dotnet")], taxonomy.Keys.ToArray());
            IReadOnlyList<SitemapEntry> sitemap = await new SitemapQueries(db).GetAsync(new CultureKey("en"), TestContext.Current.CancellationToken);
            Assert.DoesNotContain(sitemap, e => e.Path.Contains("/legacy", StringComparison.Ordinal));

            ContentImportResult third = await service.ImportAsync(a.Root, TestContext.Current.CancellationToken);

            Assert.Equal(ContentImportStatus.Imported, third.Status);
            Assert.Equal(2, await db.Stacks.CountAsync(s => s.RetiredAt == null, TestContext.Current.CancellationToken));
        }
    }

    private static ContentWorkspace WithLegacyStack(ContentWorkspace source)
    {
        ContentWorkspace copy = source.Clone();
        const string Legacy = """
            - key: legacy
              locales:
                ru:
                  title: Legacy
                  description: Старый стек.
                en:
                  title: Legacy
                  description: An old stack.
            """;
        copy.Write("stacks.yaml", copy.Read("stacks.yaml") + Legacy + Environment.NewLine);
        return copy;
    }

    [Fact]
    public async Task Import_RetiredTopicStaysRetiredOnLaterImports()
    {
        (TrainerDbContext db, ContentImportService service, _) = await NewScopeAsync();
        await using (db)
        {
            using ContentWorkspace a = ContentWorkspace.CopyOfValid();
            using ContentWorkspace b = WithoutCsharpTopic(a);
            using ContentWorkspace c = b.Clone();
            c.Edit("dotnet/async/configureawait-false-purpose.yaml", "ConfigureAwait", "ConfigureAwait (v2)");
            await service.ImportAsync(a.Root, TestContext.Current.CancellationToken);
            await service.ImportAsync(b.Root, TestContext.Current.CancellationToken);
            DateTimeOffset firstRetiredAt = (await db.Topics.AsNoTracking().SingleAsync(t => t.Key == "csharp", TestContext.Current.CancellationToken)).RetiredAt!.Value;

            ContentImportResult result = await service.ImportAsync(c.Root, TestContext.Current.CancellationToken);

            Assert.Equal(ContentImportStatus.Imported, result.Status);
            TopicEntity topic = await db.Topics.AsNoTracking().SingleAsync(t => t.Key == "csharp", TestContext.Current.CancellationToken);
            Assert.Equal(firstRetiredAt, topic.RetiredAt);
        }
    }

    [Fact]
    public async Task Import_ReaddedFile_ClearsRetired()
    {
        (TrainerDbContext db, ContentImportService service, _) = await NewScopeAsync();
        await using (db)
        {
            using ContentWorkspace a = ContentWorkspace.CopyOfValid();
            using ContentWorkspace b = a.Clone();
            b.Delete(ClosureFile);
            await service.ImportAsync(a.Root, TestContext.Current.CancellationToken);
            await service.ImportAsync(b.Root, TestContext.Current.CancellationToken);

            ContentImportResult result = await service.ImportAsync(a.Root, TestContext.Current.CancellationToken);

            Assert.Equal(ContentImportStatus.Imported, result.Status);
            Assert.Equal(0, result.Retired);
            Assert.Equal(6, await db.Questions.CountAsync(q => q.RetiredAt == null, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Import_SlugChangedWithoutRedirect_IsRejected()
    {
        (TrainerDbContext db, ContentImportService service, _) = await NewScopeAsync();
        await using (db)
        {
            using ContentWorkspace a = ContentWorkspace.CopyOfValid();
            using ContentWorkspace b = WithRenamedClosure(a, addRedirect: false);
            await service.ImportAsync(a.Root, TestContext.Current.CancellationToken);

            ContentImportResult result = await service.ImportAsync(b.Root, TestContext.Current.CancellationToken);

            Assert.Equal(ContentImportStatus.Rejected, result.Status);
            Assert.Contains(result.Issues, i => i.Code == "slug-changed");
            Assert.Equal("closure-for-loop", (await db.Questions.AsNoTracking().SingleAsync(q => q.Id == ClosureId, TestContext.Current.CancellationToken)).Slug);
        }
    }

    [Fact]
    public async Task Import_NewIdReusingRetiredSlug_IsRejected()
    {
        (TrainerDbContext db, ContentImportService service, _) = await NewScopeAsync();
        await using (db)
        {
            using ContentWorkspace a = ContentWorkspace.CopyOfValid();
            using ContentWorkspace b = a.Clone();
            b.Delete(ClosureFile);
            using ContentWorkspace c = b.Clone();
            c.Write(ClosureFile, a.Read(ClosureFile).Replace(ClosureId, "01KBZ3Q8N9V2K3M4P5R6S7T8WZ", StringComparison.Ordinal));
            await service.ImportAsync(a.Root, TestContext.Current.CancellationToken);
            await service.ImportAsync(b.Root, TestContext.Current.CancellationToken);

            ContentImportResult result = await service.ImportAsync(c.Root, TestContext.Current.CancellationToken);

            Assert.Equal(ContentImportStatus.Rejected, result.Status);
            Assert.Contains(result.Issues, i => i.Code == "slug-reused" && i.Path == ClosureFile);
            Assert.Equal(6, await db.Questions.CountAsync(TestContext.Current.CancellationToken));
        }
    }

    private const string OtherId = "01KBZ3Q8N9V2K3M4P5R6S7T8WZ";

    private static ContentWorkspace WithClosureSlugTakenByOtherId(ContentWorkspace source)
    {
        ContentWorkspace copy = source.Clone();
        string original = copy.Read(ClosureFile);
        copy.Write(ClosureFile, original.Replace(ClosureId, OtherId, StringComparison.Ordinal));
        return copy;
    }

    [Fact]
    public async Task Import_NewIdTakingSlugOfDepartingQuestion_IsRejected()
    {
        (TrainerDbContext db, ContentImportService service, _) = await NewScopeAsync();
        await using (db)
        {
            using ContentWorkspace a = ContentWorkspace.CopyOfValid();
            using ContentWorkspace replaced = WithClosureSlugTakenByOtherId(a);
            await service.ImportAsync(a.Root, TestContext.Current.CancellationToken);

            ContentImportResult result = await service.ImportAsync(replaced.Root, TestContext.Current.CancellationToken);

            Assert.Equal(ContentImportStatus.Rejected, result.Status);
            Assert.Contains(result.Issues, i => i.Code == "slug-reused" && i.Path == ClosureFile);
            Assert.Equal(6, await db.Questions.CountAsync(q => q.RetiredAt == null, TestContext.Current.CancellationToken));
            Assert.Equal(1, await db.ContentImports.CountAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Import_RollbackOntoSlugTakenByOtherId_IsRejected()
    {
        (TrainerDbContext db, ContentImportService service, _) = await NewScopeAsync();
        await using (db)
        {
            using ContentWorkspace s1 = ContentWorkspace.CopyOfValid();
            using ContentWorkspace s2 = WithRenamedClosure(s1, addRedirect: true);
            using ContentWorkspace s3 = WithClosureSlugTakenByOtherId(s1);
            await service.ImportAsync(s1.Root, TestContext.Current.CancellationToken);
            await service.ImportAsync(s2.Root, TestContext.Current.CancellationToken);
            ContentImportResult third = await service.ImportAsync(s3.Root, TestContext.Current.CancellationToken);
            Assert.Equal(ContentImportStatus.Imported, third.Status);

            ContentImportResult result = await service.ImportAsync(s1.Root, TestContext.Current.CancellationToken);

            Assert.Equal(ContentImportStatus.Rejected, result.Status);
            Assert.Contains(result.Issues, i => i.Code == "slug-reused");
            Assert.Equal(third.Hash, (await db.ContentImports.SingleAsync(i => i.IsActive, TestContext.Current.CancellationToken)).Hash);
            Assert.Equal("closure-in-for-loop", (await db.Questions.AsNoTracking().SingleAsync(q => q.Id == ClosureId, TestContext.Current.CancellationToken)).Slug);
        }
    }

    [Fact]
    public async Task Import_Success_InvalidatesCache()
    {
        (TrainerDbContext db, ContentImportService service, FakeInvalidator invalidator) = await NewScopeAsync();
        await using (db)
        {
            using ContentWorkspace good = ContentWorkspace.CopyOfValid();
            using ContentWorkspace bad = good.Clone();
            bad.Edit(ClosureFile, "level: junior", "level: wizard");

            await service.ImportAsync(good.Root, TestContext.Current.CancellationToken);
            Assert.Equal(1, invalidator.Calls);

            await service.ImportAsync(good.Root, TestContext.Current.CancellationToken);
            await service.ImportAsync(bad.Root, TestContext.Current.CancellationToken);
            Assert.Equal(1, invalidator.Calls);
        }
    }

    [Fact]
    public async Task Import_UniqueViolationAtWriteTime_IsRejectedWithoutPartialWrites()
    {
        string connectionString = await postgres.CreateDatabaseAsync();
        await using TrainerDbContext seedDb = PostgresFixture.CreateContext(connectionString);
        using ContentWorkspace a = ContentWorkspace.CopyOfValid();
        using ContentWorkspace b = a.Clone();
        string extra = b.Read("dotnet/async/result-deadlock-ui-thread.yaml")
            .Replace("01KBZ3Q8N9V2K3M4P5R6S7T8WC", "01KBZ3Q8N9V2K3M4P5R6S7T8XA", StringComparison.Ordinal)
            .Replace("slug: result-deadlock-ui-thread", "slug: extra-question", StringComparison.Ordinal);
        b.Write("dotnet/async/extra-question.yaml", extra);
        await CreateService(seedDb, new FakeInvalidator()).ImportAsync(a.Root, TestContext.Current.CancellationToken);
        string activeBefore = (await seedDb.ContentImports.AsNoTracking().SingleAsync(i => i.IsActive, TestContext.Current.CancellationToken)).Hash;

        // The pre-checks run before the write, so a row that appears in between is exactly what the database constraint must catch.
        RaceInterceptor race = new(connectionString, "extra-question");
        DbContextOptionsBuilder<TrainerDbContext> options = new();
        TrainerDbContextOptions.Configure(options, connectionString);
        options.AddInterceptors(race);
        await using TrainerDbContext racingDb = new(options.Options);
        FakeInvalidator invalidator = new();

        ContentImportResult result = await CreateService(racingDb, invalidator).ImportAsync(b.Root, TestContext.Current.CancellationToken);

        Assert.True(race.Fired);
        Assert.Equal(ContentImportStatus.Rejected, result.Status);
        ImportIssue issue = Assert.Single(result.Issues);
        Assert.Equal("db-unique-violation", issue.Code);
        Assert.Contains("ix_questions_stack_key_slug", issue.Message, StringComparison.Ordinal);
        Assert.Equal(0, invalidator.Calls);
        Assert.Equal(activeBefore, (await seedDb.ContentImports.AsNoTracking().SingleAsync(i => i.IsActive, TestContext.Current.CancellationToken)).Hash);
        Assert.Equal(1, await seedDb.ContentImports.CountAsync(TestContext.Current.CancellationToken));
        List<string> ids = await seedDb.Questions.AsNoTracking().Where(q => q.Slug == "extra-question").Select(q => q.Id).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal([RaceInterceptor.CompetitorId], ids);
        Assert.Equal(7, await seedDb.Questions.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(12, await seedDb.QuestionLocales.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Import_WritePhaseThrows_TheSameServiceCanImportAgain()
    {
        string connectionString = await postgres.CreateDatabaseAsync();
        FailOnceInterceptor failure = new("INSERT INTO question_locales");
        DbContextOptionsBuilder<TrainerDbContext> options = new();
        TrainerDbContextOptions.Configure(options, connectionString);
        options.AddInterceptors(failure);
        await using TrainerDbContext db = new(options.Options);
        ContentImportService service = CreateService(db, new FakeInvalidator());
        using ContentWorkspace content = ContentWorkspace.CopyOfValid();

        // The questions were saved inside the transaction, then the connection failed while the locales were written.
        await Assert.ThrowsAsync<DbUpdateException>(() => service.ImportAsync(content.Root, TestContext.Current.CancellationToken));
        Assert.True(failure.Fired);

        // A retry on the same scope must start from the database, not from entities the rollback discarded.
        ContentImportResult retried = await service.ImportAsync(content.Root, TestContext.Current.CancellationToken);

        Assert.Equal(ContentImportStatus.Imported, retried.Status);
        Assert.Equal(6, await db.Questions.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(12, await db.QuestionLocales.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Import_InvalidatorThrowsAfterCommit_StillReturnsImportedAndLogsAnError()
    {
        string connectionString = await postgres.CreateDatabaseAsync();
        await using TrainerDbContext db = PostgresFixture.CreateContext(connectionString);
        CapturingLogger logger = new();
        ContentImportService service = CreateService(db, new ThrowingInvalidator(), logger: logger);
        using ContentWorkspace content = ContentWorkspace.CopyOfValid();

        ContentImportResult result = await service.ImportAsync(content.Root, TestContext.Current.CancellationToken);

        Assert.Equal(ContentImportStatus.Imported, result.Status);
        Assert.Equal(result.Hash, (await db.ContentImports.AsNoTracking().SingleAsync(i => i.IsActive, TestContext.Current.CancellationToken)).Hash);
        Assert.Equal(6, await db.Questions.CountAsync(TestContext.Current.CancellationToken));
        LogEntry entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.IsType<InvalidOperationException>(entry.Exception);
    }

    [Fact]
    public async Task Import_CancelledDuringInvalidation_Propagates_WhileOtherFailuresStayLogged()
    {
        string connectionString = await postgres.CreateDatabaseAsync();
        await using TrainerDbContext db = PostgresFixture.CreateContext(connectionString);
        using CancellationTokenSource stopping = new();
        CancelingInvalidator invalidator = new(stopping);
        ContentImportService service = CreateService(db, invalidator);
        using ContentWorkspace content = ContentWorkspace.CopyOfValid();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ImportAsync(content.Root, stopping.Token));

        Assert.Equal(1, await db.ContentImports.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Import_MalformedFile_IsRejectedWithoutThrowing()
    {
        (TrainerDbContext db, ContentImportService service, _) = await NewScopeAsync();
        await using (db)
        {
            using ContentWorkspace content = ContentWorkspace.CopyOfValid();
            content.CopyIn(Path.Combine(ContentWorkspace.BrokenFixtureRoot, "dotnet", "csharp", "bad-date.yaml"), "dotnet/csharp/bad-date.yaml");

            ContentImportResult result = await service.ImportAsync(content.Root, TestContext.Current.CancellationToken);

            Assert.Equal(ContentImportStatus.Rejected, result.Status);
            Assert.Contains(result.Issues, i => i.Path.EndsWith("bad-date.yaml", StringComparison.Ordinal));
            Assert.Equal(0, await db.Questions.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(0, await db.ContentImports.CountAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Import_ConcurrentCalls_OneImportsOtherUnchanged()
    {
        string connectionString = await postgres.CreateDatabaseAsync();
        await using TrainerDbContext firstDb = PostgresFixture.CreateContext(connectionString);
        await using TrainerDbContext secondDb = PostgresFixture.CreateContext(connectionString);
        using ContentWorkspace content = ContentWorkspace.CopyOfValid();
        using Barrier barrier = new(2);
        ContentImportService first = CreateService(firstDb, new FakeInvalidator(), new BarrierReader(new ContentReader(), barrier));
        ContentImportService second = CreateService(secondDb, new FakeInvalidator(), new BarrierReader(new ContentReader(), barrier));

        ContentImportResult[] results = await Task.WhenAll(
            Task.Run(() => first.ImportAsync(content.Root, TestContext.Current.CancellationToken)),
            Task.Run(() => second.ImportAsync(content.Root, TestContext.Current.CancellationToken)));

        Assert.Single(results, r => r.Status == ContentImportStatus.Imported);
        Assert.Single(results, r => r.Status == ContentImportStatus.Unchanged);
        await using TrainerDbContext check = PostgresFixture.CreateContext(connectionString);
        Assert.Equal(1, await check.ContentImports.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(6, await check.Questions.CountAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Holds both importers after reading until each has arrived, so they reach the transaction together.</summary>
    private sealed class BarrierReader(IContentReader inner, Barrier barrier) : IContentReader
    {
        public async Task<ContentSet> ReadAsync(string rootPath, CancellationToken ct)
        {
            ContentSet set = await inner.ReadAsync(rootPath, ct);
            bool bothArrived = await Task.Run(() => barrier.SignalAndWait(TimeSpan.FromSeconds(30)), ct);
            Assert.True(bothArrived);
            return set;
        }
    }

    private sealed class FakeInvalidator : IContentCacheInvalidator
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task InvalidateAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.CompletedTask;
        }
    }

    /// <summary>Inserts a question with a taken slug from another connection right before the importer writes its first question.</summary>
    private sealed class RaceInterceptor(string connectionString, string slug) : DbCommandInterceptor
    {
        public const string CompetitorId = "01KBZ3Q8N9V2K3M4P5R6S7TZZZ";

        private int _fired;

        public bool Fired => Volatile.Read(ref _fired) == 1;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await MaybeRaceAsync(command, cancellationToken);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await MaybeRaceAsync(command, cancellationToken);
            return result;
        }

        private async Task MaybeRaceAsync(DbCommand command, CancellationToken ct)
        {
            if (!command.CommandText.Contains("INSERT INTO questions", StringComparison.OrdinalIgnoreCase)
                || Interlocked.Exchange(ref _fired, 1) == 1)
            {
                return;
            }

            await using TrainerDbContext other = PostgresFixture.CreateContext(connectionString);
            other.Questions.Add(new QuestionEntity
            {
                Id = CompetitorId,
                StackKey = "dotnet",
                TopicKey = "async",
                Slug = slug,
                Verified = new DateOnly(2026, 11, 11),
            });
            await other.SaveChangesAsync(ct);
        }
    }

    /// <summary>Throws once, before the first command whose text contains <paramref name="marker"/>, as a dropped connection would.</summary>
    private sealed class FailOnceInterceptor(string marker) : DbCommandInterceptor
    {
        private int _fired;

        public bool Fired => Volatile.Read(ref _fired) == 1;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            ThrowOnce(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            ThrowOnce(command);
            return ValueTask.FromResult(result);
        }

        private void ThrowOnce(DbCommand command)
        {
            if (command.CommandText.Contains(marker, StringComparison.OrdinalIgnoreCase) && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                throw new InvalidOperationException("The connection was lost.");
            }
        }
    }

    private sealed class CancelingInvalidator(CancellationTokenSource source) : IContentCacheInvalidator
    {
        public async Task InvalidateAsync(CancellationToken ct)
        {
            await source.CancelAsync();
            ct.ThrowIfCancellationRequested();
        }
    }

    private sealed class ThrowingInvalidator : IContentCacheInvalidator
    {
        public Task InvalidateAsync(CancellationToken ct) => throw new InvalidOperationException("cache store is down");
    }

    private sealed record LogEntry(LogLevel Level, Exception? Exception);

    private sealed class CapturingLogger : ILogger<ContentImportService>
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add(new LogEntry(logLevel, exception));
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
