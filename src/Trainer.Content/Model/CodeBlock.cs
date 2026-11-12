namespace Trainer.Content.Model;

/// <summary>Language-neutral code shown with a question; <see cref="Harness"/> tells the snippet runner how to wrap it.</summary>
public sealed record CodeBlock
{
    public required string Language { get; init; }
    public required string Harness { get; init; }
    public required string Source { get; init; }
}
