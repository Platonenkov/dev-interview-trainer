using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.Unicode;
using Trainer.Application.Abstractions;
using Trainer.Application.Content;
using Trainer.Application.Stacks;
using Trainer.Application.StaticSite;
using Trainer.Contracts;
using Trainer.Domain.Keys;

namespace Trainer.Web.StaticSite;

/// <summary>
/// The bytes of one stack's content shards in one culture.
/// </summary>
/// <param name="IndexPath">Site-relative path of the index, <c>data/{culture}/{stack}/index.{hash}.json</c>.</param>
/// <param name="Files">Question files by site-relative path, <c>data/{culture}/q/{id}.{hash}.json</c>.</param>
public sealed record StaticContentFiles(string IndexPath, ReadOnlyMemory<byte> Index, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Files);

/// <summary>
/// Builds the content shards of the static edition from the imported content and keeps them until the next import
/// (ADR 0023). An entry is valid only for the content hash it was built from, so a build that raced an import is never
/// served as current, and the invalidator clears everything after each import anyway.
/// </summary>
public sealed class StaticContentCatalog(IServiceScopeFactory scopes) : IContentCacheInvalidator, IDisposable
{
    // An import landing between reading the hash and reading the questions would pair new questions with an old hash.
    private const int MaxBuildAttempts = 3;

    // Cyrillic stays readable in the file and keeps Russian files near their UTF-8 size; markup characters are still escaped.
    private static readonly JsonSerializerOptions Options = new(TrainerJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.Cyrillic),
    };

    private static readonly JsonTypeInfo<QuestionDto> QuestionInfo = (JsonTypeInfo<QuestionDto>)Options.GetTypeInfo(typeof(QuestionDto));
    private static readonly JsonTypeInfo<StaticIndexDto> IndexInfo = (JsonTypeInfo<StaticIndexDto>)Options.GetTypeInfo(typeof(StaticIndexDto));

    private readonly ConcurrentDictionary<(string Culture, string Stack), Entry> _entries = new();
    private readonly SemaphoreSlim _build = new(1, 1);

    // The active content hash is read from the database once per import, not once per request: only an import changes it,
    // and every import ends in InvalidateAsync. The generation stops a read that started before an import from being
    // stored after the invalidation that import triggered.
    private readonly object _hashGate = new();
    private string? _activeHash;
    private long _generation;

    /// <summary>The shards of the stack for the culture, built from the active import and cached for it.</summary>
    /// <exception cref="InvalidOperationException">No content has been imported yet.</exception>
    public async Task<StaticContentFiles> GetAsync(CultureKey culture, StackKey stack, CancellationToken ct)
    {
        (string, string) key = (culture.Value, stack.Value);
        string active = await ActiveHashAsync(ct);
        if (_entries.TryGetValue(key, out Entry? cached) && string.Equals(cached.ContentHash, active, StringComparison.Ordinal))
        {
            return cached.Files;
        }

        await _build.WaitAsync(ct);
        try
        {
            if (_entries.TryGetValue(key, out cached) && string.Equals(cached.ContentHash, active, StringComparison.Ordinal))
            {
                return cached.Files;
            }

            Entry built = await BuildAsync(culture, stack, ct);
            _entries[key] = built;
            return built.Files;
        }
        finally
        {
            _build.Release();
        }
    }

    /// <summary>Drops every built shard; the next request builds from the new import.</summary>
    public Task InvalidateAsync(CancellationToken ct)
    {
        lock (_hashGate)
        {
            _generation++;
            _activeHash = null;
        }

        _entries.Clear();
        return Task.CompletedTask;
    }

    public void Dispose() => _build.Dispose();

    private async Task<string> ActiveHashAsync(CancellationToken ct)
    {
        long generation;
        lock (_hashGate)
        {
            if (_activeHash is { } known)
            {
                return known;
            }

            generation = _generation;
        }

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        string hash = await scope.ServiceProvider.GetRequiredService<IContentStatusQueries>().GetActiveHashAsync(ct)
            ?? throw new InvalidOperationException("No content has been imported yet.");
        lock (_hashGate)
        {
            if (generation == _generation)
            {
                _activeHash = hash;
            }
        }

        return hash;
    }

    private async Task<Entry> BuildAsync(CultureKey culture, StackKey stack, CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IContentStatusQueries status = scope.ServiceProvider.GetRequiredService<IContentStatusQueries>();
        IStaticSiteQueries queries = scope.ServiceProvider.GetRequiredService<IStaticSiteQueries>();
        IStackQueries stacks = scope.ServiceProvider.GetRequiredService<IStackQueries>();

        for (int attempt = 0; attempt < MaxBuildAttempts; attempt++)
        {
            string before = await status.GetActiveHashAsync(ct) ?? throw new InvalidOperationException("No content has been imported yet.");
            IReadOnlyList<QuestionDto> questions = await queries.GetLiveQuestionsAsync(culture, stack, ct);
            IReadOnlyList<TopicTitle> topics = await stacks.GetTopicTitlesAsync(culture, stack, ct);
            string after = await status.GetActiveHashAsync(ct) ?? string.Empty;
            if (string.Equals(before, after, StringComparison.Ordinal))
            {
                return Create(culture, stack, before, questions, topics);
            }
        }

        throw new InvalidOperationException("The content kept changing while the static shards were built.");
    }

    private static Entry Create(
        CultureKey culture, StackKey stack, string contentHash, IReadOnlyList<QuestionDto> questions, IReadOnlyList<TopicTitle> topics)
    {
        Dictionary<string, ReadOnlyMemory<byte>> files = new(questions.Count, StringComparer.Ordinal);
        List<StaticIndexEntryDto> entries = new(questions.Count);
        foreach (QuestionDto question in questions)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(question, QuestionInfo);
            string hash = StaticContentHash.Of(bytes);
            files[StaticContentPaths.Question(culture.Value, question.Id, hash)] = bytes;
            entries.Add(new StaticIndexEntryDto(question.Id, question.Slug, question.Topic, question.Level, question.Type, question.Title, hash));
        }

        StaticIndexDto index = new(
            StaticIndexDto.CurrentFormat,
            culture.Value,
            stack.Value,
            contentHash,
            // Ordinal by key, not the UI's culture-aware title order: that order depends on the build machine's ICU, and these
            // bytes are named after their hash. The island sorts for display.
            [.. topics.OrderBy(t => t.Key.Value, StringComparer.Ordinal).Select(t => new TopicDto(t.Key.Value, t.Title))],
            entries);
        byte[] indexBytes = JsonSerializer.SerializeToUtf8Bytes(index, IndexInfo);
        string indexPath = StaticContentPaths.Index(culture.Value, stack.Value, StaticContentHash.Of(indexBytes));
        return new Entry(contentHash, new StaticContentFiles(indexPath, indexBytes, files));
    }

    private sealed record Entry(string ContentHash, StaticContentFiles Files);
}
