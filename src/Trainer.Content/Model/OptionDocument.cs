namespace Trainer.Content.Model;

/// <summary>Language-neutral part of an answer option; its text lives in each locale block under the same id.</summary>
public sealed record OptionDocument
{
    public required string Id { get; init; }
    public required bool Correct { get; init; }
    public Expectation? Expect { get; init; }
}
