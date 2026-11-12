namespace Trainer.Infrastructure.Persistence.Entities;

internal sealed class TopicLocaleEntity
{
    public required string StackKey { get; set; }
    public required string TopicKey { get; set; }
    public required string Culture { get; set; }
    public required string Title { get; set; }
    public required string Description { get; set; }
}
