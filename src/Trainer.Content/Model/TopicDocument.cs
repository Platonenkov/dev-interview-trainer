namespace Trainer.Content.Model;

/// <summary>One entry of a stack topics file.</summary>
public sealed record TopicDocument
{
    public required string Key { get; init; }
    public required IReadOnlyDictionary<string, LocaleTextDocument> Locales { get; init; }
}
