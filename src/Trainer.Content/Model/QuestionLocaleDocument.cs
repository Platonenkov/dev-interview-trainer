namespace Trainer.Content.Model;

/// <summary>Localised text of a question. A question is published in a culture only when this block exists and validates.</summary>
public sealed record QuestionLocaleDocument
{
    public required string Title { get; init; }
    public required string Prompt { get; init; }
    public string? Hint { get; init; }
    public IReadOnlyDictionary<string, string>? Options { get; init; }
    public IReadOnlyDictionary<string, string>? OptionExplanations { get; init; }
    public required string Answer { get; init; }
    public required string Explanation { get; init; }
    public required IReadOnlyList<string> LinkTitles { get; init; }
}
