namespace Trainer.Content.Model;

/// <summary>Machine-checked result of running code: stdout, an exception type name, a compile error or nondeterminism.</summary>
public sealed record Expectation
{
    public required string Kind { get; init; }
    public string? Value { get; init; }
}
