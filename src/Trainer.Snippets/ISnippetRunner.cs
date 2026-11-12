using Trainer.Content.Model;

namespace Trainer.Snippets;

/// <summary>
/// Builds a snippet once and runs the build any number of times, so nondeterminism checks do not pay for repeated compilation.
/// Both methods throw <see cref="OperationCanceledException"/> when <c>ct</c> is cancelled and leave nothing running.
/// </summary>
public interface ISnippetRunner
{
    Task<SnippetBuild> BuildAsync(CodeBlock code, CancellationToken ct);
    Task<SnippetResult> RunAsync(SnippetBuild build, CancellationToken ct);
}

/// <summary>A finished build. Owns the host directories; dispose it when no more runs are needed.</summary>
public sealed class SnippetBuild : IAsyncDisposable
{
    private readonly Func<ValueTask>? _cleanup;

    private SnippetBuild(string? outputDirectory, SnippetResult? failure, Func<ValueTask>? cleanup)
    {
        OutputDirectory = outputDirectory;
        Failure = failure;
        _cleanup = cleanup;
    }

    /// <summary>Host directory that holds the compiled output; null when the build failed.</summary>
    public string? OutputDirectory { get; }

    /// <summary>A <c>compile-error</c> or <c>sandbox-error</c> result when the build failed; null when it succeeded.</summary>
    public SnippetResult? Failure { get; }

    public static SnippetBuild Succeeded(string outputDirectory, Func<ValueTask>? cleanup = null) =>
        new(outputDirectory, null, cleanup);

    public static SnippetBuild Failed(SnippetResult failure) => new(null, failure, null);

    public ValueTask DisposeAsync() => _cleanup?.Invoke() ?? ValueTask.CompletedTask;
}
