using Trainer.Domain.Keys;

namespace Trainer.Application.Stacks;

public interface IStackQueries
{
    /// <summary>The stacks that exist and, for each, the keys of its topics (empty when it has none), ordered by key.</summary>
    Task<IReadOnlyDictionary<StackKey, IReadOnlyList<TopicKey>>> GetTaxonomyAsync(CancellationToken ct);
}
