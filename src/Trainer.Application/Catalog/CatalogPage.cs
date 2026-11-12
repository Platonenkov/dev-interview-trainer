using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;

namespace Trainer.Application.Catalog;

/// <summary>
/// One page of the catalog plus the facet counts for the filter panel. <paramref name="Page"/> is the page actually
/// returned: a requested page past the end is clamped to the last one.
/// </summary>
public sealed record CatalogPage(
    IReadOnlyList<CatalogRow> Rows,
    int Total,
    int Page,
    int PageCount,
    IReadOnlyList<FacetCount> LevelCounts,
    IReadOnlyList<FacetCount> TopicCounts,
    IReadOnlyList<FacetCount> TypeCounts);

public sealed record CatalogRow(
    string Slug,
    string Title,
    Level Level,
    TopicKey Topic,
    string TopicTitle,
    QuestionType Type,
    IReadOnlyList<string> Tags);

/// <summary>
/// A filter value with the number of questions it would show. <paramref name="Key"/> is the URL key; for topics
/// <paramref name="Title"/> is the localised topic title, for levels and types it equals the key and the UI localises it.
/// </summary>
public sealed record FacetCount(string Key, string Title, int Count);
