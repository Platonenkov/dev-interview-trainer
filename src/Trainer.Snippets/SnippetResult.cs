namespace Trainer.Snippets;

/// <summary>Outcome of building or running a snippet. <see cref="Value"/> is stdout, an exception type, a compile error code or sandbox diagnostics depending on <see cref="Kind"/>.</summary>
public sealed record SnippetResult(string Kind, string? Value)
{
    public const string KindOutput = "output";
    public const string KindException = "exception";
    public const string KindCompileError = "compile-error";
    public const string KindTimeout = "timeout";
    public const string KindSandboxError = "sandbox-error";
    public const string KindNondeterministic = "nondeterministic";
}
