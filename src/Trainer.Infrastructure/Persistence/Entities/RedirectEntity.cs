namespace Trainer.Infrastructure.Persistence.Entities;

internal sealed class RedirectEntity
{
    public required string StackKey { get; set; }
    public required string FromSlug { get; set; }
    public required string ToSlug { get; set; }
}
