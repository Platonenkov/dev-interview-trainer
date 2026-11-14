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
}
