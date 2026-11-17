using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using Trainer.Application.Abstractions;
using Trainer.Application.Content;
using Trainer.Content.Model;
using Trainer.Content.Reading;
using Trainer.Content.Rendering;
using Trainer.Content.Schema;
using Trainer.Content.Validation;
using Trainer.Infrastructure.Persistence;
using Trainer.Infrastructure.Persistence.Entities;

namespace Trainer.Infrastructure.Content;

/// <summary>
/// Imports a content root into PostgreSQL. Reading and importing run in one transaction guarded by an advisory lock
/// taken first, so two importers (two app instances, or the CLI next to the app) cannot interleave and a second one
/// queues behind an import that is still reading files.
/// </summary>
public sealed partial class ContentImportService : IContentImportService
{
    // Arbitrary constant shared by every importer; transaction-scoped, so it is released on commit or rollback.
    private const long AdvisoryLockKey = 0x4449_5443_4F4E_5431L;

    private const string SlugChangedCode = "slug-changed";
    private const string SlugReusedCode = "slug-reused";
    private const string UniqueViolationCode = "db-unique-violation";
    private const string UniqueViolationSqlState = "23505";
    private const int MaxRedirectHops = 32;

    private readonly TrainerDbContext _db;
    private readonly IContentReader _reader;
    private readonly IContentValidator _validator;
    private readonly IContentCacheInvalidator _cacheInvalidator;
    private readonly TimeProvider _time;
    private readonly ILogger<ContentImportService> _logger;
    private readonly QuestionRowMapper _mapper;

    public ContentImportService(
        TrainerDbContext db,
        IContentReader reader,
        IContentValidator validator,
        IContentRenderer renderer,
        IContentCacheInvalidator cacheInvalidator,
        TimeProvider time,
        ILogger<ContentImportService> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(cacheInvalidator);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _db = db;
        _reader = reader;
        _validator = validator;
        _cacheInvalidator = cacheInvalidator;
        _time = time;
        _logger = logger;
        _mapper = new QuestionRowMapper(renderer);
    }

    public async Task<ContentImportResult> ImportAsync(string contentRoot, CancellationToken ct)
    {
        ContentImportResult result;
        try
        {
            await using IDbContextTransaction transaction = await _db.Database.BeginTransactionAsync(ct);
            // Imports are ordered against each other here, and the link target of the content root is resolved once
            // inside the lock by the reader. A switch of the link by a deployment while this read runs therefore cannot
            // mix two releases; it only makes the next, queued import the one that picks up the new release.
            await _db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({AdvisoryLockKey})", ct);
            ContentSet content = await _reader.ReadAsync(contentRoot, ct);
            result = await TryImportLockedAsync(content, ct);
            if (result.Status == ContentImportStatus.Imported)
            {
                await transaction.CommitAsync(ct);
            }
            else
            {
                await transaction.RollbackAsync(ct);
            }
        }
        finally
        {
            // Also after a failure mid-write: the rolled-back rows would otherwise stay tracked as saved or added, and a
            // retry on the same scope would collide with them instead of starting from the database.
            _db.ChangeTracker.Clear();
        }
        if (result.Status == ContentImportStatus.Imported)
        {
            await InvalidateCachesAsync(result, ct);
        }

        return result;
    }

    /// <summary>
    /// The content is already committed, so a failing cache invalidation must not turn the import into a failure:
    /// the caller would report an error for content that is in fact live. The cached pages expire on their own.
    /// Cancellation of the caller's own token is not a cache failure and propagates.
    /// </summary>
    private async Task InvalidateCachesAsync(ContentImportResult result, CancellationToken ct)
    {
        try
        {
            await _cacheInvalidator.InvalidateAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogInvalidationFailed(ex, result.Hash);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Content {Hash} was imported, but invalidating the content caches failed; cached pages may stay stale until they expire.")]
    private partial void LogInvalidationFailed(Exception ex, string hash);

    /// <summary>
    /// Safety net: the pre-checks should already reject slug collisions, but a unique-index violation that slips
    /// through must still end as a rejected import, not as an exception that leaves the host without content.
    /// </summary>
    private async Task<ContentImportResult> TryImportLockedAsync(ContentSet content, CancellationToken ct)
    {
        try
        {
            return await ImportLockedAsync(content, ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolationSqlState } postgres)
        {
            ImportIssue issue = new("", UniqueViolationCode, $"The database rejected the content with a unique constraint violation ({postgres.ConstraintName}).");
            return new ContentImportResult(ContentImportStatus.Rejected, content.Hash, 0, 0, [issue]);
        }
    }

    private async Task<ContentImportResult> ImportLockedAsync(ContentSet content, CancellationToken ct)
    {
        ContentImportEntity? active = await _db.ContentImports.AsNoTracking().SingleOrDefaultAsync(i => i.IsActive, ct);
        if (active is not null && active.Hash == content.Hash && active.RenderVersion == ContentSchema.RenderVersion)
        {
            return new ContentImportResult(ContentImportStatus.Unchanged, content.Hash, 0, 0, []);
        }

        List<ImportIssue> issues = await CollectIssuesAsync(content, ct);
        if (issues.Count > 0)
        {
            return new ContentImportResult(ContentImportStatus.Rejected, content.Hash, 0, 0, issues);
        }

        int retired = await WriteAsync(content, ct);
        return new ContentImportResult(ContentImportStatus.Imported, content.Hash, content.Questions.Count, retired, []);
    }

    private async Task<List<ImportIssue>> CollectIssuesAsync(ContentSet content, CancellationToken ct)
    {
        List<ContentIssue> contentIssues = [.. content.ReadIssues, .. _validator.Validate(content)];
        List<ImportIssue> issues = [.. contentIssues.Select(ToImportIssue)];

        if (contentIssues.Count == 0)
        {
            // Content that was accepted before (a rollback to an older commit) must stay importable even if the
            // slugs it carries differ from what the database holds now, so only the rename check is skipped.
            bool previouslyAccepted = await _db.ContentImports.AnyAsync(i => i.Hash == content.Hash, ct);
            issues.AddRange(await FindSlugIssuesAsync(content, checkRenames: !previouslyAccepted, ct));
        }

        return issues;
    }

    private async Task<List<ImportIssue>> FindSlugIssuesAsync(ContentSet content, bool checkRenames, CancellationToken ct)
    {
        List<ExistingSlug> existing = await _db.Questions.AsNoTracking()
            .Select(q => new ExistingSlug(q.Id, q.StackKey, q.Slug))
            .ToListAsync(ct);
        Dictionary<string, ExistingSlug> byId = existing.ToDictionary(q => q.Id, StringComparer.Ordinal);
        Dictionary<(string Stack, string Slug), ExistingSlug> bySlug = existing.ToDictionary(q => (q.StackKey, q.Slug));
        Dictionary<string, string> incomingSlugs = content.Questions.ToDictionary(q => q.Document.Id, q => q.Document.Slug, StringComparer.Ordinal);

        List<ImportIssue> issues = [];
        foreach (SourcedQuestion question in content.Questions)
        {
            QuestionDocument document = question.Document;
            if (checkRenames
                && byId.TryGetValue(document.Id, out ExistingSlug? current)
                && !string.Equals(current.Slug, document.Slug, StringComparison.Ordinal)
                && !HasRedirect(content, document.Stack, current.Slug, document.Slug))
            {
                issues.Add(new ImportIssue(question.RelativePath, SlugChangedCode,
                    $"The slug of question {document.Id} changed from '{current.Slug}' to '{document.Slug}' without a redirect."));
            }

            // A holder that stays in the content under another slug vacates this one; any other holder (retired,
            // removed, or simply a different id) would collide with the unique (stack, slug) index.
            if (bySlug.TryGetValue((document.Stack, document.Slug), out ExistingSlug? holder)
                && !string.Equals(holder.Id, document.Id, StringComparison.Ordinal)
                && !(incomingSlugs.TryGetValue(holder.Id, out string? holderNewSlug) && !string.Equals(holderNewSlug, document.Slug, StringComparison.Ordinal)))
            {
                issues.Add(new ImportIssue(question.RelativePath, SlugReusedCode,
                    $"The slug '{document.Slug}' still belongs to question {holder.Id}, (retired, removed or not vacated); it cannot be taken over."));
            }
        }

        return issues;
    }

    private static bool HasRedirect(ContentSet content, string stack, string oldSlug, string newSlug)
    {
        if (!content.RedirectsByStack.TryGetValue(stack, out IReadOnlyList<RedirectDocument>? redirects))
        {
            return false;
        }

        Dictionary<string, string> targets = new(StringComparer.Ordinal);
        foreach (RedirectDocument redirect in redirects)
        {
            targets[SlugOf(redirect.From)] = SlugOf(redirect.To);
        }

        string slug = oldSlug;
        for (int hop = 0; hop < MaxRedirectHops && targets.TryGetValue(slug, out string? next); hop++)
        {
            if (string.Equals(next, newSlug, StringComparison.Ordinal))
            {
                return true;
            }

            slug = next;
        }

        return false;
    }

    /// <summary>Redirect entries are topic/slug paths; slugs are unique per stack, so the slug alone identifies the target.</summary>
    private static string SlugOf(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static ImportIssue ToImportIssue(ContentIssue issue) =>
        new(issue.RelativePath, issue.Code, issue.JsonPath is null ? issue.Message : $"{issue.Message} ({issue.JsonPath})");

    private async Task<int> WriteAsync(ContentSet content, CancellationToken ct)
    {
        DateTimeOffset now = _time.GetUtcNow();

        await UpsertTaxonomyAsync(content, now, ct);
        List<string> questionIds = [.. content.Questions.Select(q => q.Document.Id)];
        await UpsertQuestionsAsync(content, questionIds, ct);
        int retired = await _db.Questions
            .Where(q => q.RetiredAt == null && !questionIds.Contains(q.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(q => q.RetiredAt, now), ct);
        await ReplaceRedirectsAsync(content, ct);
        await ActivateImportAsync(content, now, ct);
        return retired;
    }

    private async Task UpsertTaxonomyAsync(ContentSet content, DateTimeOffset now, CancellationToken ct)
    {
        List<StackEntity> storedStacks = await _db.Stacks.ToListAsync(ct);
        HashSet<string> knownStacks = storedStacks.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        RetireVanishedStacks(content, storedStacks, now);
        foreach (StackDocument stack in content.Stacks.Where(s => !knownStacks.Contains(s.Key)))
        {
            _db.Stacks.Add(new StackEntity { Key = stack.Key });
        }

        List<TopicEntity> storedTopics = await _db.Topics.ToListAsync(ct);
        HashSet<(string Stack, string Topic)> knownTopics = storedTopics.Select(t => (t.StackKey, t.Key)).ToHashSet();
        RetireVanishedTopics(content, storedTopics, now);
        foreach ((string stack, IReadOnlyList<TopicDocument> topics) in content.TopicsByStack)
        {
            foreach (TopicDocument topic in topics.Where(t => !knownTopics.Contains((stack, t.Key))))
            {
                _db.Topics.Add(new TopicEntity { StackKey = stack, Key = topic.Key });
            }
        }

        await _db.SaveChangesAsync(ct);

        List<string> stackKeys = [.. content.Stacks.Select(s => s.Key)];
        await _db.StackLocales.Where(l => stackKeys.Contains(l.StackKey)).ExecuteDeleteAsync(ct);
        foreach (StackDocument stack in content.Stacks)
        {
            foreach ((string culture, LocaleTextDocument text) in stack.Locales)
            {
                _db.StackLocales.Add(new StackLocaleEntity { StackKey = stack.Key, Culture = culture, Title = text.Title, Description = text.Description });
            }
        }

        foreach ((string stack, IReadOnlyList<TopicDocument> topics) in content.TopicsByStack)
        {
            List<string> topicKeys = [.. topics.Select(t => t.Key)];
            await _db.TopicLocales.Where(l => l.StackKey == stack && topicKeys.Contains(l.TopicKey)).ExecuteDeleteAsync(ct);
            foreach (TopicDocument topic in topics)
            {
                foreach ((string culture, LocaleTextDocument text) in topic.Locales)
                {
                    _db.TopicLocales.Add(new TopicLocaleEntity
                    {
                        StackKey = stack,
                        TopicKey = topic.Key,
                        Culture = culture,
                        Title = text.Title,
                        Description = text.Description,
                    });
                }
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Stacks follow the same rule as topics: retired when gone, never deleted, revived when they return.</summary>
    private static void RetireVanishedStacks(ContentSet content, List<StackEntity> storedStacks, DateTimeOffset now)
    {
        HashSet<string> inContent = content.Stacks.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        foreach (StackEntity stack in storedStacks)
        {
            stack.RetiredAt = inContent.Contains(stack.Key) ? null : stack.RetiredAt ?? now;
        }
    }

    /// <summary>
    /// Topics are retired like questions, never deleted: a rollback to older content must find the row again, and retired
    /// questions keep their foreign key to it. A topic that is back in the content is revived.
    /// </summary>
    private static void RetireVanishedTopics(ContentSet content, List<TopicEntity> storedTopics, DateTimeOffset now)
    {
        HashSet<(string Stack, string Topic)> inContent = content.TopicsByStack
            .SelectMany(pair => pair.Value.Select(topic => (pair.Key, topic.Key)))
            .ToHashSet();
        foreach (TopicEntity topic in storedTopics)
        {
            topic.RetiredAt = inContent.Contains((topic.StackKey, topic.Key)) ? null : topic.RetiredAt ?? now;
        }
    }

    private async Task UpsertQuestionsAsync(ContentSet content, List<string> questionIds, CancellationToken ct)
    {
        Dictionary<string, QuestionEntity> existing = await _db.Questions
            .Where(q => questionIds.Contains(q.Id))
            .ToDictionaryAsync(q => q.Id, StringComparer.Ordinal, ct);

        foreach (SourcedQuestion question in content.Questions)
        {
            QuestionDocument document = question.Document;
            if (!existing.TryGetValue(document.Id, out QuestionEntity? entity))
            {
                entity = new QuestionEntity { Id = document.Id };
                _db.Questions.Add(entity);
            }

            _mapper.Apply(entity, document);
            entity.RetiredAt = null;
        }

        await _db.SaveChangesAsync(ct);

        await _db.QuestionLocales.Where(l => questionIds.Contains(l.QuestionId)).ExecuteDeleteAsync(ct);
        foreach (SourcedQuestion question in content.Questions)
        {
            _db.QuestionLocales.AddRange(_mapper.BuildLocales(question.Document, question.Document.Id));
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task ReplaceRedirectsAsync(ContentSet content, CancellationToken ct)
    {
        await _db.Redirects.ExecuteDeleteAsync(ct);
        foreach ((string stack, IReadOnlyList<RedirectDocument> redirects) in content.RedirectsByStack)
        {
            foreach (RedirectDocument redirect in redirects)
            {
                _db.Redirects.Add(new RedirectEntity { StackKey = stack, FromSlug = SlugOf(redirect.From), ToSlug = SlugOf(redirect.To) });
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task ActivateImportAsync(ContentSet content, DateTimeOffset now, CancellationToken ct)
    {
        await _db.ContentImports.Where(i => i.IsActive).ExecuteUpdateAsync(s => s.SetProperty(i => i.IsActive, false), ct);
        _db.ContentImports.Add(new ContentImportEntity
        {
            Hash = content.Hash,
            SchemaVersion = content.SchemaVersion,
            RenderVersion = ContentSchema.RenderVersion,
            ImportedAt = now,
            QuestionCount = content.Questions.Count,
            IsActive = true,
        });
        await _db.SaveChangesAsync(ct);
    }

    private sealed record ExistingSlug(string Id, string StackKey, string Slug);
}
