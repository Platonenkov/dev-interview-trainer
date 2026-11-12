namespace Trainer.Content.Model;

/// <summary>Localised title and description of a stack or topic.</summary>
public sealed record LocaleTextDocument
{
    public required string Title { get; init; }
    public required string Description { get; init; }
}
