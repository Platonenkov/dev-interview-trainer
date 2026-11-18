using Trainer.Domain.Keys;

namespace Trainer.Application.Catalog;

public interface ICatalogQueries
{
    /// <summary>
    /// Live (not retired) questions that have a locale for the requested culture, ordered by level then title.
    /// Each facet is counted with the filters of the other two dimensions applied but not its own, so a visitor
    /// sees what a different choice in that dimension would yield. Topics with no matching question are omitted
    /// unless they are selected. When <see cref="CatalogQuery.Search"/> is set, the rows and every facet count only
    /// questions whose title in the culture contains it (case-insensitive, literal) or that carry the tag it spells.
    /// </summary>
    Task<CatalogPage> GetAsync(CatalogQuery query, CancellationToken ct);

    /// <summary>
    /// Every live question of the stack in the culture on one page (<c>Page = 1</c>, <c>PageCount = 1</c>), in the order of
    /// <see cref="GetAsync"/>, with the facets of an empty query. The static edition renders this once and filters it in
    /// the browser, because a static host cannot run a query.
    /// </summary>
    Task<CatalogPage> GetAllAsync(CultureKey culture, StackKey stack, CancellationToken ct);
}
