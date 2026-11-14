using Microsoft.EntityFrameworkCore;
using Trainer.Application.Catalog;
using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;
using Trainer.Infrastructure.Persistence;
using Trainer.Infrastructure.Persistence.Entities;

namespace Trainer.Infrastructure.Queries;

internal sealed class CatalogQueries(TrainerDbContext db) : ICatalogQueries
{
    public async Task<CatalogPage> GetAsync(CatalogQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        IQueryable<QuestionEntity> live = ApplySearch(LiveQuestions.Of(db, query.Culture, query.Stack), query);
        Level[] levels = [.. query.Levels];
        string[] topics = [.. query.Topics.Select(t => t.Value)];
        QuestionType[] types = [.. query.Types];

        IQueryable<QuestionEntity> matching = Filter(live, levels, topics, types);
        int total = await matching.CountAsync(ct);
        int pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)CatalogQuery.PageSize));
        int page = Math.Clamp(query.Page, 1, pageCount);

        Dictionary<string, string> topicTitles = await LiveQuestions.TopicTitlesAsync(db, query.Culture, query.Stack, ct);
        IReadOnlyList<CatalogRow> rows = await ReadRowsAsync(matching, query.Culture, page, topicTitles, ct);

        IReadOnlyList<FacetCount> levelCounts = await CountLevelsAsync(Filter(live, [], topics, types), ct);
        IReadOnlyList<FacetCount> topicCounts = await CountTopicsAsync(Filter(live, levels, [], types), topics, topicTitles, query.Culture, ct);
        IReadOnlyList<FacetCount> typeCounts = await CountTypesAsync(Filter(live, levels, topics, []), ct);

        return new CatalogPage(rows, total, page, pageCount, levelCounts, topicCounts, typeCounts);
    }

    /// <summary>
    /// Narrows the live set before the facets are derived from it, so every count reflects the search. The term is a
    /// bound parameter and its LIKE wildcards are escaped, so it only ever matches literally; a tag has to match
    /// exactly (kebab-case, spaces turned into hyphens), which keeps "async" from pulling in every "async-*" tag.
    /// </summary>
    private IQueryable<QuestionEntity> ApplySearch(IQueryable<QuestionEntity> live, CatalogQuery query)
    {
        if (string.IsNullOrEmpty(query.Search))
        {
            return live;
        }

        string pattern = "%" + EscapeLike(query.Search) + "%";
        string tag = query.Search.ToLowerInvariant().Replace(' ', '-');
        string culture = query.Culture.Value;
        return live.Where(q =>
            q.Tags.Contains(tag)
            || db.QuestionLocales.Any(l => l.QuestionId == q.Id && l.Culture == culture && EF.Functions.ILike(EF.Functions.Collate(l.Title, Collations.IcuSearch), pattern, LikeEscape)));
    }

    private const string LikeEscape = "\\";

    private static string EscapeLike(string term) =>
        term.Replace(LikeEscape, LikeEscape + LikeEscape, StringComparison.Ordinal)
            .Replace("%", LikeEscape + "%", StringComparison.Ordinal)
            .Replace("_", LikeEscape + "_", StringComparison.Ordinal);

    private static IQueryable<QuestionEntity> Filter(
        IQueryable<QuestionEntity> source, Level[] levels, string[] topics, QuestionType[] types)
    {
        IQueryable<QuestionEntity> result = source;
        if (levels.Length > 0)
        {
            result = result.Where(q => levels.Contains(q.Level));
        }

        if (topics.Length > 0)
        {
            result = result.Where(q => topics.Contains(q.TopicKey));
        }

        if (types.Length > 0)
        {
            result = result.Where(q => types.Contains(q.Type));
        }

        return result;
    }

    private async Task<IReadOnlyList<CatalogRow>> ReadRowsAsync(
        IQueryable<QuestionEntity> matching,
        CultureKey culture,
        int page,
        IReadOnlyDictionary<string, string> topicTitles,
        CancellationToken ct)
    {
        string cultureValue = culture.Value;
        var projected = await (
                from q in matching
                join l in db.QuestionLocales on q.Id equals l.QuestionId
                where l.Culture == cultureValue
                orderby q.Level, l.Title, q.Slug
                select new { q.Id, q.Slug, l.Title, q.Level, q.TopicKey, q.Type, q.Tags })
            .Skip((page - 1) * CatalogQuery.PageSize)
            .Take(CatalogQuery.PageSize)
            .ToListAsync(ct);

        return [.. projected.Select(r => new CatalogRow(
            r.Slug, r.Title, r.Level, new TopicKey(r.TopicKey), LiveQuestions.TitleOf(topicTitles, r.TopicKey), r.Type, r.Tags,
            new QuestionId(r.Id)))];
    }

    private static async Task<IReadOnlyList<FacetCount>> CountLevelsAsync(IQueryable<QuestionEntity> source, CancellationToken ct)
    {
        Dictionary<Level, int> counts = await source
            .GroupBy(q => q.Level)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return [.. LevelKeys.All.Select(level =>
        {
            string key = LevelKeys.ToKey(level);
            return new FacetCount(key, key, counts.GetValueOrDefault(level));
        })];
    }

    private static async Task<IReadOnlyList<FacetCount>> CountTypesAsync(IQueryable<QuestionEntity> source, CancellationToken ct)
    {
        Dictionary<QuestionType, int> counts = await source
            .GroupBy(q => q.Type)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return [.. QuestionTypeKeys.All.Select(type =>
        {
            string key = QuestionTypeKeys.ToKey(type);
            return new FacetCount(key, key, counts.GetValueOrDefault(type));
        })];
    }

    private static async Task<IReadOnlyList<FacetCount>> CountTopicsAsync(
        IQueryable<QuestionEntity> source,
        string[] selected,
        IReadOnlyDictionary<string, string> titles,
        CultureKey culture,
        CancellationToken ct)
    {
        Dictionary<string, int> counts = await source
            .GroupBy(q => q.TopicKey)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, StringComparer.Ordinal, ct);

        // A selected topic stays listed even at zero so that the visitor can still unselect it.
        foreach (string key in selected)
        {
            counts.TryAdd(key, 0);
        }

        return LiveQuestions.TopicFacets(counts, titles, culture);
    }
}
