using Microsoft.EntityFrameworkCore;
using Trainer.Application.Catalog;
using Trainer.Application.Topics;
using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;
using Trainer.Infrastructure.Persistence;
using Trainer.Infrastructure.Persistence.Entities;

namespace Trainer.Infrastructure.Queries;

internal sealed class TopicQueries(TrainerDbContext db) : ITopicQueries
{
    public async Task<TopicPageModel?> GetAsync(CultureKey culture, StackKey stack, TopicKey topic, CancellationToken ct)
    {
        string stackValue = stack.Value;
        string topicValue = topic.Value;
        string cultureValue = culture.Value;
        TopicLocaleEntity? locale = await db.TopicLocales
            .AsNoTracking()
            .FirstOrDefaultAsync(
                l => l.StackKey == stackValue
                    && l.TopicKey == topicValue
                    && l.Culture == cultureValue
                    && db.Stacks.Any(s => s.Key == l.StackKey && s.RetiredAt == null)
                    && db.Topics.Any(t => t.StackKey == l.StackKey && t.Key == l.TopicKey && t.RetiredAt == null),
                ct);
        if (locale is null)
        {
            return null;
        }

        IQueryable<QuestionEntity> live = LiveQuestions.Of(db, culture, stack).Where(q => q.TopicKey == topicValue);
        Dictionary<Level, int> counts = await live
            .GroupBy(q => q.Level)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        FacetCount[] levelCounts = [.. LevelKeys.All.Select(level =>
        {
            string key = LevelKeys.ToKey(level);
            return new FacetCount(key, key, counts.GetValueOrDefault(level));
        })];

        var projected = await (
                from q in live
                join l in db.QuestionLocales on q.Id equals l.QuestionId
                where l.Culture == cultureValue
                orderby q.Level, l.Title, q.Slug
                select new { q.Id, q.Slug, l.Title, q.Level, q.Type, q.Tags })
            .Take(CatalogQuery.PageSize)
            .ToListAsync(ct);

        CatalogRow[] rows = [.. projected.Select(r => new CatalogRow(
            r.Slug, r.Title, r.Level, topic, locale.Title, r.Type, r.Tags, new QuestionId(r.Id)))];
        List<string> localeCultures = await db.TopicLocales
            .AsNoTracking()
            .Where(l => l.StackKey == stackValue && l.TopicKey == topicValue)
            .Select(l => l.Culture)
            .ToListAsync(ct);
        CultureKey[] available = [.. CultureKey.Supported.Where(c => localeCultures.Contains(c.Value))];
        return new TopicPageModel(topic, locale.Title, locale.Description, levelCounts, rows, available);
    }
}
