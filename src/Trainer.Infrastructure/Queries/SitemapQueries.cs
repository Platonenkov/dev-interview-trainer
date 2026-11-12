using Microsoft.EntityFrameworkCore;
using Trainer.Application.Seo;
using Trainer.Domain.Keys;
using Trainer.Infrastructure.Persistence;

namespace Trainer.Infrastructure.Queries;

internal sealed class SitemapQueries(TrainerDbContext db) : ISitemapQueries
{
    public async Task<IReadOnlyList<SitemapEntry>> GetAsync(CultureKey culture, CancellationToken ct)
    {
        string cultureValue = culture.Value;
        string prefix = "/" + cultureValue;

        List<SitemapEntry> entries =
        [
            new(prefix, CultureKey.Supported, null),
            new(prefix + "/about", CultureKey.Supported, null),
        ];

        var stackLocales = await db.StackLocales.AsNoTracking()
            .Where(l => db.Stacks.Any(s => s.Key == l.StackKey && s.RetiredAt == null))
            .Select(l => new { l.StackKey, l.Culture })
            .ToListAsync(ct);
        var topicLocales = await db.TopicLocales.AsNoTracking()
            .Where(l => db.Topics.Any(t => t.StackKey == l.StackKey && t.Key == l.TopicKey && t.RetiredAt == null))
            .Select(l => new { l.StackKey, l.TopicKey, l.Culture })
            .ToListAsync(ct);
        var questionLocales = await (
                from q in db.Questions.AsNoTracking()
                where q.RetiredAt == null
                    && db.Topics.Any(t => t.StackKey == q.StackKey && t.Key == q.TopicKey && t.RetiredAt == null)
                join l in db.QuestionLocales on q.Id equals l.QuestionId
                select new { q.StackKey, q.TopicKey, q.Slug, q.Verified, l.Culture })
            .ToListAsync(ct);

        foreach (IGrouping<string, string> stack in stackLocales
            .GroupBy(l => l.StackKey, l => l.Culture, StringComparer.Ordinal)
            .Where(g => g.Contains(cultureValue, StringComparer.Ordinal))
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            CultureKey[] stackCultures = CulturesOf(stack);
            string stackPath = $"{prefix}/{stack.Key}";
            entries.Add(new SitemapEntry(stackPath, stackCultures, null));
            entries.Add(new SitemapEntry(stackPath + "/questions", stackCultures, null));

            Dictionary<string, DateOnly> topicModified = questionLocales
                .Where(q => q.StackKey == stack.Key && q.Culture == cultureValue)
                .GroupBy(q => q.TopicKey, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Max(q => q.Verified), StringComparer.Ordinal);

            foreach (IGrouping<string, string> topic in topicLocales
                .Where(l => l.StackKey == stack.Key)
                .GroupBy(l => l.TopicKey, l => l.Culture, StringComparer.Ordinal)
                .Where(g => g.Contains(cultureValue, StringComparer.Ordinal))
                .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                // A topic page without a live question in this culture is served noindex, so it stays out of the sitemap.
                if (!topicModified.TryGetValue(topic.Key, out DateOnly modified))
                {
                    continue;
                }

                HashSet<string> withQuestions = questionLocales
                    .Where(q => q.StackKey == stack.Key && q.TopicKey == topic.Key)
                    .Select(q => q.Culture)
                    .ToHashSet(StringComparer.Ordinal);
                entries.Add(new SitemapEntry($"{stackPath}/topics/{topic.Key}", CulturesOf(topic.Where(withQuestions.Contains)), modified));
            }

            foreach (var question in questionLocales
                .Where(q => q.StackKey == stack.Key)
                .GroupBy(q => q.Slug, StringComparer.Ordinal)
                .Where(g => g.Any(q => q.Culture == cultureValue))
                .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                entries.Add(new SitemapEntry(
                    $"{stackPath}/questions/{question.Key}",
                    CulturesOf(question.Select(q => q.Culture)),
                    question.First().Verified));
            }
        }

        return entries;
    }

    private static CultureKey[] CulturesOf(IEnumerable<string> cultures)
    {
        HashSet<string> present = [.. cultures];
        return [.. CultureKey.Supported.Where(c => present.Contains(c.Value))];
    }
}
