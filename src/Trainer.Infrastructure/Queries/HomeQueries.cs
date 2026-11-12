using Microsoft.EntityFrameworkCore;
using Trainer.Application.Catalog;
using Trainer.Application.Home;
using Trainer.Domain.Keys;
using Trainer.Infrastructure.Persistence;
using Trainer.Infrastructure.Persistence.Entities;

namespace Trainer.Infrastructure.Queries;

internal sealed class HomeQueries(TrainerDbContext db) : IHomeQueries
{
    public async Task<HomeStats> GetStatsAsync(CultureKey culture, StackKey stack, CancellationToken ct)
    {
        IQueryable<QuestionEntity> live = LiveQuestions.Of(db, culture, stack);

        Dictionary<string, int> topicCounts = await live
            .GroupBy(q => q.TopicKey)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, StringComparer.Ordinal, ct);
        int typeCount = await live.Select(q => q.Type).Distinct().CountAsync(ct);
        Dictionary<string, string> titles = await LiveQuestions.TopicTitlesAsync(db, culture, stack, ct);

        FacetCount[] topics = LiveQuestions.TopicFacets(topicCounts, titles);
        return new HomeStats(topicCounts.Values.Sum(), topics.Length, typeCount, topics);
    }
}
