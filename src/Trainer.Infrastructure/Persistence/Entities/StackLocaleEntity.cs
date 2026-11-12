namespace Trainer.Infrastructure.Persistence.Entities;

internal sealed class StackLocaleEntity
{
    public required string StackKey { get; set; }
    public required string Culture { get; set; }
    public required string Title { get; set; }
    public required string Description { get; set; }
}
