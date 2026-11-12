namespace Trainer.Content.Model;

/// <summary>
/// One question file as authored. String-valued keys (level, type, grading and so on) stay strings here;
/// their allowed values are enforced by the validator and listed in the generated JSON schema.
/// </summary>
public sealed record QuestionDocument
{
    public required string Id { get; init; }
    public required string Slug { get; init; }
    public required string Stack { get; init; }
    public required string Topic { get; init; }
    public required string Level { get; init; }
    public required string Type { get; init; }
    public required string Grading { get; init; }
    public required IReadOnlyList<string> Tags { get; init; }
    public CodeBlock? Code { get; init; }
    public IReadOnlyList<IReadOnlyList<int>>? BugLineSets { get; init; }
    public string? FixedSource { get; init; }
    public Expectation? FixedExpect { get; init; }
    public Expectation? Expect { get; init; }
    public IReadOnlyList<string>? AcceptedOutputs { get; init; }
    public IReadOnlyList<OptionDocument>? Options { get; init; }
    public required IReadOnlyList<LinkDocument> Links { get; init; }
    public required IReadOnlyDictionary<string, QuestionLocaleDocument> Locales { get; init; }
    public required QuestionMeta Meta { get; init; }
}
