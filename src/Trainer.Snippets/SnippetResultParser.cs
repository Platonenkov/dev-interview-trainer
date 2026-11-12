using System.Text.RegularExpressions;

namespace Trainer.Snippets;

/// <summary>Turns raw process results into <see cref="SnippetResult"/>; pure so the rules are testable without Docker.</summary>
public static partial class SnippetResultParser
{
    private const int DiagnosticsLimit = 2000;

    [GeneratedRegex(@"^Unhandled exception\. (?<type>[\w.]+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex UnhandledException();

    [GeneratedRegex(@"error (?<code>CS\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex CompilerError();

    public static SnippetResult ParseBuild(int exitCode, string output)
    {
        if (exitCode == 0)
        {
            return new SnippetResult(SnippetResult.KindOutput, null);
        }

        Match error = CompilerError().Match(output);
        if (error.Success)
        {
            return new SnippetResult(SnippetResult.KindCompileError, error.Groups["code"].Value);
        }

        return new SnippetResult(SnippetResult.KindSandboxError, $"build exit code {exitCode}: {Clip(output)}");
    }

    /// <summary>
    /// Checks run in a fixed order: a deadline or truncated output makes everything else unreliable, so they win over the
    /// exit code. An exit code other than 0 without an unhandled-exception line is infrastructure (OOM kill, stack overflow,
    /// <c>Environment.Exit</c>), never content.
    /// </summary>
    public static SnippetResult ParseRun(int exitCode, string stdout, string stderr, bool killedByDeadline, bool truncated)
    {
        if (killedByDeadline)
        {
            return new SnippetResult(SnippetResult.KindTimeout, null);
        }

        if (truncated)
        {
            return new SnippetResult(SnippetResult.KindSandboxError, "output exceeded the capture limit");
        }

        Match unhandled = UnhandledException().Match(stderr);
        if (unhandled.Success)
        {
            return new SnippetResult(SnippetResult.KindException, unhandled.Groups["type"].Value);
        }

        if (exitCode == 0)
        {
            return new SnippetResult(SnippetResult.KindOutput, Normalize(stdout));
        }

        return new SnippetResult(SnippetResult.KindSandboxError, $"run exit code {exitCode}: {Clip(stderr)}");
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private static string Clip(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length <= DiagnosticsLimit ? trimmed : trimmed[..DiagnosticsLimit];
    }
}
