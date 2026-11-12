namespace Trainer.Content.Model;

/// <summary>One entry of the root stacks file.</summary>
public sealed record StackDocument
{
    public required string Key { get; init; }
    public required IReadOnlyDictionary<string, LocaleTextDocument> Locales { get; init; }
}
