using Trainer.Contracts;
using Trainer.Domain.Keys;

namespace Trainer.Application.StaticSite;

/// <summary>
/// What the static edition's export needs from the imported content: the question files and the pages to crawl.
/// </summary>
public interface IStaticSiteQueries
{
    /// <summary>
    /// Every live question of the stack that has a locale for the culture, ordered by id (ordinal), each exactly the
    /// <see cref="QuestionDto"/> a server session returns for it. Retired questions and those without the locale are left out.
    /// </summary>
    Task<IReadOnlyList<QuestionDto>> GetLiveQuestionsAsync(CultureKey culture, StackKey stack, CancellationToken ct);

    /// <summary>The logical page paths to export and the logical paths that answer a redirect.</summary>
    Task<StaticPagePaths> GetPagePathsAsync(CancellationToken ct);
}

/// <summary>
/// Logical paths (culture-rooted, unprefixed, unslashed), each list ordered ordinally without duplicates.
/// <paramref name="Pages"/> are the sitemap entries of every culture plus the pages of retired questions, which stay
/// readable but are not in the sitemap. <paramref name="Redirects"/> are the question pages of renamed slugs, once per culture;
/// they answer a 301 and never appear in <paramref name="Pages"/>.
/// </summary>
public sealed record StaticPagePaths(IReadOnlyList<string> Pages, IReadOnlyList<string> Redirects);
