namespace Trainer.Content.Model;

/// <summary>A source link; <see cref="Lang"/> is the language of the linked page, not of the question.</summary>
public sealed record LinkDocument
{
    public required string Url { get; init; }
    public required string Lang { get; init; }
    public required string Kind { get; init; }
}
