using System.Collections.Frozen;
using Trainer.Domain.Keys;

namespace Trainer.Web.ContentState;

/// <summary>
/// Immutable view of which stacks and topics exist. Middleware and cache policies read only this, so a cache hit
/// never touches the database. <see cref="Generation"/> grows with every reload and is meant for cache keys.
/// </summary>
public sealed class ContentSnapshot
{
    public static ContentSnapshot Empty { get; } = new(0, new Dictionary<StackKey, IReadOnlyList<TopicKey>>());

    private readonly FrozenDictionary<string, FrozenSet<string>> _topicsByStack;

    public ContentSnapshot(long generation, IReadOnlyDictionary<StackKey, IReadOnlyList<TopicKey>> taxonomy)
    {
        ArgumentNullException.ThrowIfNull(taxonomy);
        Generation = generation;
        _topicsByStack = taxonomy.ToFrozenDictionary(
            pair => pair.Key.Value,
            pair => pair.Value.Select(t => t.Value).ToFrozenSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    public long Generation { get; }

    public IReadOnlyCollection<string> Stacks => _topicsByStack.Keys;

    public bool HasStack(string? stack) => stack is not null && _topicsByStack.ContainsKey(stack);

    public bool HasTopic(string? stack, string? topic) =>
        stack is not null && topic is not null && _topicsByStack.TryGetValue(stack, out FrozenSet<string>? topics) && topics.Contains(topic);

    public IReadOnlyCollection<string> TopicsOf(string stack) =>
        _topicsByStack.TryGetValue(stack, out FrozenSet<string>? topics) ? topics.Items.ToArray() : [];
}
