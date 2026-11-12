namespace Trainer.Infrastructure.Persistence.Entities;

internal sealed class TopicEntity
{
    public required string StackKey { get; set; }
    public required string Key { get; set; }

    /// <summary>Set when the topic disappeared from the content; the row is kept so that a rollback can restore it.</summary>
    public DateTimeOffset? RetiredAt { get; set; }
}
