using System.Text;
using Trainer.Content.Reading;
using Trainer.ContentCli.Links;

namespace Trainer.ContentCli.Commands;

/// <summary>
/// Checks every external source link of the question bank. Exit code 1 means at least one link is dead; links that could
/// not be judged (blocked, throttled, timed out) are listed but do not fail the run.
/// </summary>
/// <param name="summaryPath">GitHub step summary file; the report is appended there as a Markdown table when set.</param>
internal sealed class CheckLinksCommand(
    IContentReader reader,
    LinkCollector collector,
    Func<HttpClient> clientFactory,
    LinkCheckOptions options,
    string? summaryPath,
    TextWriter output,
    TextWriter error) : ICliCommand
{
    private const string ContentOption = "--content";

    public string Name => "check-links";

    public string Usage => "check-links [--content <dir>]";

    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        string? root = ParseRoot(args);
        if (root is null)
        {
            error.WriteLine($"Usage: {Usage}");
            return ExitCodes.UsageError;
        }

        ContentSet set;
        try
        {
            set = await reader.ReadAsync(root, ct);
        }
        catch (DirectoryNotFoundException ex)
        {
            error.WriteLine(ex.Message);
            return ExitCodes.UsageError;
        }

        if (set.ReadIssues.Count > 0)
        {
            new IssueWriter(output).Write(set.ReadIssues);
            error.WriteLine("Content files could not be read; fix them before checking links (see 'validate').");
            return ExitCodes.ContentIssues;
        }

        IReadOnlyDictionary<string, IReadOnlyList<string>> citations = collector.Collect(set);
        IReadOnlyList<LinkCheckResult> results;
        try
        {
            using HttpClient client = clientFactory();
            results = await new LinkChecker(client, options).CheckAllAsync(citations.Keys, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            error.WriteLine("Cancelled.");
            return ExitCodes.Cancelled;
        }

        List<LinkCheckResult> problems = [.. results.Where(r => r.Status != LinkStatus.Ok)];
        foreach (LinkCheckResult problem in problems)
        {
            output.WriteLine($"{Label(problem.Status)}: {problem.Url} ({problem.Detail}) cited by {string.Join(", ", citations[problem.Url])}");
        }

        int dead = problems.Count(p => p.Status == LinkStatus.Dead);
        int unknown = problems.Count - dead;
        string totals = $"Checked {results.Count} URL(s): {results.Count - problems.Count} ok, {dead} dead, {unknown} unknown.";
        output.WriteLine(totals);
        await WriteSummaryAsync(totals, problems, citations, ct);

        if (dead > 0)
        {
            error.WriteLine($"{dead} dead link(s) found.");
            return ExitCodes.ContentIssues;
        }

        return ExitCodes.Ok;
    }

    private static string? ParseRoot(IReadOnlyList<string> args) => args.Count switch
    {
        0 => ContentRoot.DefaultPath,
        2 when string.Equals(args[0], ContentOption, StringComparison.Ordinal) && args[1].Length > 0 => args[1],
        _ => null,
    };

    private static string Label(LinkStatus status) => status == LinkStatus.Dead ? "dead" : "unknown";

    private async Task WriteSummaryAsync(
        string totals, List<LinkCheckResult> problems, IReadOnlyDictionary<string, IReadOnlyList<string>> citations, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(summaryPath))
        {
            return;
        }

        StringBuilder markdown = new();
        markdown.AppendLine("## Link check").AppendLine().AppendLine(totals);
        if (problems.Count > 0)
        {
            markdown.AppendLine().AppendLine("| Status | URL | Detail | Cited by |").AppendLine("| --- | --- | --- | --- |");
            foreach (LinkCheckResult problem in problems)
            {
                string files = string.Join("<br>", citations[problem.Url].Select(EscapeCell));
                markdown.AppendLine(string.Concat("| ", Label(problem.Status), " | ", EscapeCell(problem.Url), " | ", EscapeCell(problem.Detail), " | ", files, " |"));
            }
        }

        try
        {
            await File.AppendAllTextAsync(summaryPath, markdown.ToString(), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The summary is a convenience; the exit code and stdout already carry the result.
            error.WriteLine($"Could not write the step summary: {ex.Message}");
        }
    }

    internal static string EscapeCell(string text) => text
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("|", "\\|", StringComparison.Ordinal)
        .Replace("`", "\\`", StringComparison.Ordinal)
        .Replace("*", "\\*", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal)
        .Replace("~", "\\~", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal)
        .Replace("]", "\\]", StringComparison.Ordinal)
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\r\n", " ", StringComparison.Ordinal)
        .Replace('\r', ' ')
        .Replace('\n', ' ');
}
