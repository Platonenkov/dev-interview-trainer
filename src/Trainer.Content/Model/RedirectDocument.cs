namespace Trainer.Content.Model;

/// <summary>A renamed slug: <see cref="From"/> and <see cref="To"/> are topic/slug paths within the stack.</summary>
public sealed record RedirectDocument
{
    public required string From { get; init; }
    public required string To { get; init; }
}
