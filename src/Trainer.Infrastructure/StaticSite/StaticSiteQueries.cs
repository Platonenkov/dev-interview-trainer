using Microsoft.EntityFrameworkCore;
using Trainer.Application.Seo;
using Trainer.Application.StaticSite;
using Trainer.Contracts;
using Trainer.Domain.Keys;
using Trainer.Infrastructure.Persistence;
using Trainer.Infrastructure.Queries;
using Trainer.Infrastructure.Sessions;

namespace Trainer.Infrastructure.StaticSite;

/// <summary>
/// Reads the imported content for the static export. Question files reuse <see cref="SessionRows"/>, the code that builds
/// a session's questions, so a shard can never drift from what a server session would return.
/// </summary>
internal sealed class StaticSiteQueries(TrainerDbContext db, ISitemapQueries sitemap) : IStaticSiteQueries
{
    public async Task<IReadOnlyList<QuestionDto>> GetLiveQuestionsAsync(CultureKey culture, StackKey stack, CancellationToken ct)
    {
        List<SessionRow> rows = await SessionRows.Candidates(db, LiveQuestions.Of(db, culture, stack), culture)
            .Select(SessionRows.Project)
            .ToListAsync(ct);
        Dictionary<string, string> titles = await LiveQuestions.TopicTitlesAsync(db, culture, stack, ct);

        // Sorted here, not in SQL: the database collation is not ordinal, and the order is part of the files' bytes.
        rows.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return [.. rows.Select(r => SessionRows.ToDto(r, titles))];
    }

    public async Task<StaticPagePaths> GetPagePathsAsync(CancellationToken ct)
    {
        var redirects = await db.Redirects.AsNoTracking().Select(r => new { r.StackKey, r.FromSlug }).ToListAsync(ct);
        var retired = await (
                from q in db.Questions.AsNoTracking()
                where q.RetiredAt != null
                join l in db.QuestionLocales on q.Id equals l.QuestionId
                select new { q.StackKey, q.Slug, l.Culture })
            .ToListAsync(ct);

        HashSet<string> pages = new(StringComparer.Ordinal);
        HashSet<string> redirectPaths = new(StringComparer.Ordinal);
        foreach (CultureKey culture in CultureKey.Supported)
        {
            foreach (SitemapEntry entry in await sitemap.GetAsync(culture, ct))
            {
                pages.Add(entry.Path);
            }

            foreach (var question in retired.Where(q => string.Equals(q.Culture, culture.Value, StringComparison.Ordinal)))
            {
                pages.Add(QuestionPath(culture, question.StackKey, question.Slug));
            }

            foreach (var redirect in redirects)
            {
                redirectPaths.Add(QuestionPath(culture, redirect.StackKey, redirect.FromSlug));
            }
        }

        // A redirect wins over the page of the same slug (QuestionPage), so that path is exported as a redirect only.
        pages.ExceptWith(redirectPaths);
        return new StaticPagePaths(Ordered(pages), Ordered(redirectPaths));
    }

    private static string QuestionPath(CultureKey culture, string stack, string slug) => $"/{culture.Value}/{stack}/questions/{slug}";

    private static string[] Ordered(HashSet<string> paths)
    {
        string[] ordered = [.. paths];
        Array.Sort(ordered, StringComparer.Ordinal);
        return ordered;
    }
}
