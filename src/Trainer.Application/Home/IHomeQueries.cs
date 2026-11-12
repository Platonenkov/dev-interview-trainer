using Trainer.Application.Catalog;
using Trainer.Domain.Keys;

namespace Trainer.Application.Home;

/// <summary>Figures for the home page. <paramref name="Topics"/> lists topics that have live questions in the culture, by title.</summary>
public sealed record HomeStats(int QuestionCount, int TopicCount, int TypeCount, IReadOnlyList<FacetCount> Topics);

public interface IHomeQueries
{
    Task<HomeStats> GetStatsAsync(CultureKey culture, StackKey stack, CancellationToken ct);
}
