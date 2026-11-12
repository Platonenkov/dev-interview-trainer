namespace Trainer.Content.Model;

/// <summary>Authorship and freshness; <see cref="Verified"/> is the date the content was last checked against sources.</summary>
public sealed record QuestionMeta
{
    public required IReadOnlyList<string> Authors { get; init; }
    public required DateOnly Verified { get; init; }
    public string? DotnetVersion { get; init; }
    public string? NoRunReason { get; init; }
}
