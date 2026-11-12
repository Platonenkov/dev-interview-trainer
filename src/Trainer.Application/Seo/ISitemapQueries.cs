using Trainer.Domain.Keys;

namespace Trainer.Application.Seo;

/// <summary>
/// One URL of a sitemap. <paramref name="Path"/> is an absolute path in the sitemap's culture;
/// <paramref name="Alternates"/> lists every culture the page exists in, including its own.
/// </summary>
public sealed record SitemapEntry(string Path, IReadOnlyList<CultureKey> Alternates, DateOnly? LastModified);

public interface ISitemapQueries
{
    /// <summary>
    /// The indexable pages that exist in <paramref name="culture"/>: home, about, and for every stack its home page,
    /// catalog, topics and live (not retired) questions with a locale for the culture.
    /// </summary>
    Task<IReadOnlyList<SitemapEntry>> GetAsync(CultureKey culture, CancellationToken ct);
}
