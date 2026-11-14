using Trainer.Domain.Keys;

namespace Trainer.Application.Stacks;

public interface IStackQueries
{
    /// <summary>The stacks that exist and, for each, the keys of its topics (empty when it has none), ordered by key.</summary>
    Task<IReadOnlyDictionary<StackKey, IReadOnlyList<TopicKey>>> GetTaxonomyAsync(CancellationToken ct);

    /// <summary>
    /// The live topics of <paramref name="stack"/> (topic and stack not retired) that have a title in
    /// <paramref name="culture"/>, ordered by title in that culture's collation, then by key.
    /// </summary>
    Task<IReadOnlyList<TopicTitle>> GetTopicTitlesAsync(CultureKey culture, StackKey stack, CancellationToken ct);
}
