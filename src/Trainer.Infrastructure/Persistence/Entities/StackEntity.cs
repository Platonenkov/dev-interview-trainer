namespace Trainer.Infrastructure.Persistence.Entities;

internal sealed class StackEntity
{
    public required string Key { get; set; }

    /// <summary>Set when the stack disappeared from the content; the row is kept so that a rollback can restore it.</summary>
    public DateTimeOffset? RetiredAt { get; set; }
}
