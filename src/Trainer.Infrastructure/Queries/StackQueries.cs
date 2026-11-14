using Microsoft.EntityFrameworkCore;
using Trainer.Application.Stacks;
using Trainer.Domain.Keys;
using Trainer.Infrastructure.Persistence;

namespace Trainer.Infrastructure.Queries;

internal sealed class StackQueries(TrainerDbContext db) : IStackQueries
{
    public async Task<IReadOnlyDictionary<StackKey, IReadOnlyList<TopicKey>>> GetTaxonomyAsync(CancellationToken ct)
    {
        List<string> stacks = await db.Stacks.AsNoTracking().Where(s => s.RetiredAt == null).OrderBy(s => s.Key).Select(s => s.Key).ToListAsync(ct);
        var topics = await db.Topics.AsNoTracking()
            .Where(t => t.RetiredAt == null)
            .OrderBy(t => t.Key)
            .Select(t => new { t.StackKey, t.Key })
            .ToListAsync(ct);

        Dictionary<StackKey, IReadOnlyList<TopicKey>> result = new(stacks.Count);
        foreach (string stack in stacks)
        {
            List<TopicKey> keys = topics.Where(t => t.StackKey == stack).Select(t => new TopicKey(t.Key)).ToList();
            result[new StackKey(stack)] = keys;
        }

        return result;
    }

    public async Task<IReadOnlyList<TopicTitle>> GetTopicTitlesAsync(CultureKey culture, StackKey stack, CancellationToken ct)
    {
        string cultureValue = culture.Value;
        string stackValue = stack.Value;
        var rows = await db.TopicLocales
            .AsNoTracking()
            .Where(l => l.StackKey == stackValue
                && l.Culture == cultureValue
                && db.Stacks.Any(s => s.Key == l.StackKey && s.RetiredAt == null)
                && db.Topics.Any(t => t.StackKey == l.StackKey && t.Key == l.TopicKey && t.RetiredAt == null))
            .Select(l => new { l.TopicKey, l.Title })
            .ToListAsync(ct);

        // The catalog facets and the profile sort the same way.
        return [.. rows
            .OrderBy(r => r.Title, LiveQuestions.TitleComparer(culture))
            .ThenBy(r => r.TopicKey, StringComparer.Ordinal)
            .Select(r => new TopicTitle(new TopicKey(r.TopicKey), r.Title))];
    }
}
