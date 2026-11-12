using Microsoft.EntityFrameworkCore;
using Trainer.Application.Catalog;
using Trainer.Domain.Keys;
using Trainer.Infrastructure.Persistence;
using Trainer.Infrastructure.Persistence.Entities;

namespace Trainer.Infrastructure.Queries;

/// <summary>Shared building blocks of the public catalog and home queries; everything stays in SQL.</summary>
internal static class LiveQuestions
{
    /// <summary>
    /// Questions of the stack that are not retired, belong to a stack and a topic that are not retired, and have a locale row for
    /// the culture. A question without a locale is invisible in that culture, so the catalog never links to a page that
    /// would answer 404.
    /// </summary>
    public static IQueryable<QuestionEntity> Of(TrainerDbContext db, CultureKey culture, StackKey stack)
    {
        string cultureValue = culture.Value;
        string stackValue = stack.Value;
        return db.Questions
            .AsNoTracking()
            .Where(q => q.StackKey == stackValue
                && q.RetiredAt == null
                && db.Stacks.Any(s => s.Key == q.StackKey && s.RetiredAt == null)
                && db.Topics.Any(t => t.StackKey == q.StackKey && t.Key == q.TopicKey && t.RetiredAt == null)
                && db.QuestionLocales.Any(l => l.QuestionId == q.Id && l.Culture == cultureValue));
    }

    /// <summary>Topic titles of the stack in the culture; one small query per request instead of a join per row.</summary>
    public static async Task<Dictionary<string, string>> TopicTitlesAsync(
        TrainerDbContext db, CultureKey culture, StackKey stack, CancellationToken ct)
    {
        string cultureValue = culture.Value;
        string stackValue = stack.Value;
        return await db.TopicLocales
            .AsNoTracking()
            .Where(l => l.StackKey == stackValue && l.Culture == cultureValue)
            .ToDictionaryAsync(l => l.TopicKey, l => l.Title, StringComparer.Ordinal, ct);
    }

    public static string TitleOf(IReadOnlyDictionary<string, string> titles, string topicKey) =>
        titles.TryGetValue(topicKey, out string? title) ? title : topicKey;

    public static FacetCount[] TopicFacets(
        IEnumerable<KeyValuePair<string, int>> counts, IReadOnlyDictionary<string, string> titles)
    {
        return [.. counts
            .Select(c => new FacetCount(c.Key, TitleOf(titles, c.Key), c.Value))
            .OrderBy(f => f.Title, StringComparer.CurrentCulture)
            .ThenBy(f => f.Key, StringComparer.Ordinal)];
    }
}
