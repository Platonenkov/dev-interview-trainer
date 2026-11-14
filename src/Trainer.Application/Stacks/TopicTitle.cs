using Trainer.Domain.Keys;

namespace Trainer.Application.Stacks;

/// <summary>A live topic of a stack and its title in one culture.</summary>
public sealed record TopicTitle(TopicKey Key, string Title);
