using Trainer.Application.Catalog;
using Trainer.Domain.Keys;

namespace Trainer.Application.Topics;

/// <summary>
/// <paramref name="Questions"/> is the first catalog page of the topic (up to <see cref="CatalogQuery.PageSize"/>);
/// <paramref name="AvailableCultures"/> are the cultures the topic has a title and description in.
/// </summary>
public sealed record TopicPageModel(
    TopicKey Key,
    string Title,
    string Description,
    IReadOnlyList<FacetCount> LevelCounts,
    IReadOnlyList<CatalogRow> Questions,
    IReadOnlyList<CultureKey> AvailableCultures);

public interface ITopicQueries
{
    /// <summary>Null when the topic does not exist in the stack or has no locale for the culture.</summary>
    Task<TopicPageModel?> GetAsync(CultureKey culture, StackKey stack, TopicKey topic, CancellationToken ct);
}
