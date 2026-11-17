using System.Text;
using Trainer.Content.Reading;
using Trainer.ContentCli.Links;

namespace Trainer.ContentCli.Commands;

/// <summary>
/// Checks every external source link of the question bank. Exit code 1 means at least one link is dead; links that could
/// not be judged (blocked, throttled, timed out) are listed but do not fail the run.
/// </summary>
/// <param name="summaryPath">GitHub step summary file; the report is appended there as a Markdown table when set.</param>
/// <remarks>
/// <c>--report &lt;file&gt;</c> writes the same Markdown to a file of its own, also when nothing is wrong, so the weekly
/// workflow can post it as an issue body. The file is written only after the links were judged: a missing report next to
/// exit code 1 means the run failed for another reason, not that links are dead.
/// </remarks>
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
    private const string ReportOption = "--report";

    // GitHub rejects an issue body longer than 65536 characters; the report becomes that body.
    private const int IssueBodyLimit = 65536;

    public string Name => "check-links";

    public string Usage => "check-links [--content <dir>] [--report <file>]";

    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (!TryParseArguments(args, out string root, out string? reportPath))
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
        await WriteSummaryAsync(RenderReport(totals, problems, citations), ct);
        if (reportPath is not null && !await TryWriteReportAsync(reportPath, RenderReport(totals, problems, citations, IssueBodyLimit), ct))
        {
            return ExitCodes.UsageError;
        }

        if (dead > 0)
        {
            error.WriteLine($"{dead} dead link(s) found.");
            return ExitCodes.ContentIssues;
        }

        return ExitCodes.Ok;
    }

    private static bool TryParseArguments(IReadOnlyList<string> args, out string root, out string? reportPath)
    {
        string? content = null;
        reportPath = null;
        root = string.Empty;
        if (args.Count % 2 != 0)
        {
            return false;
        }

        for (int i = 0; i < args.Count; i += 2)
        {
            string value = args[i + 1];
            // A value that looks like an option means the real value is missing ("--report --content").
            if (value.Length == 0 || value.StartsWith("--", StringComparison.Ordinal))
            {
                return false;
            }

            if (string.Equals(args[i], ContentOption, StringComparison.Ordinal) && content is null)
            {
                content = value;
            }
            else if (string.Equals(args[i], ReportOption, StringComparison.Ordinal) && reportPath is null)
            {
                reportPath = value;
            }
            else
            {
                return false;
            }
        }

        root = content ?? ContentRoot.DefaultPath;
        return true;
    }

    private static string Label(LinkStatus status) => status == LinkStatus.Dead ? "dead" : "unknown";

    /// <param name="maxLength">Rows that would push the text past this many characters are cut and counted in a closing line.</param>
    internal static string RenderReport(
        string totals, IReadOnlyList<LinkCheckResult> problems, IReadOnlyDictionary<string, IReadOnlyList<string>> citations, int maxLength = int.MaxValue)
    {
        StringBuilder markdown = new();
        markdown.AppendLine("## Link check").AppendLine().AppendLine(totals);
        if (problems.Count == 0)
        {
            return markdown.ToString();
        }

        markdown.AppendLine().AppendLine("| Status | URL | Detail | Cited by |").AppendLine("| --- | --- | --- | --- |");
        List<string> rows = [.. problems.Select(problem => string.Concat(
            "| ", Label(problem.Status), " | ", EscapeCell(problem.Url), " | ", EscapeCell(problem.Detail), " | ",
            string.Join("<br>", citations[problem.Url].Select(EscapeCell)), " |", Environment.NewLine))];
        if ((long)markdown.Length + rows.Sum(row => (long)row.Length) <= maxLength)
        {
            return markdown.Append(string.Concat(rows)).ToString();
        }

        for (int index = 0; index < rows.Count; index++)
        {
            int remaining = rows.Count - index;
            // The whole closing block (blank line, notice, line break) is reserved up front, so cutting can never overflow the limit.
            int reserve = TruncationNotice(remaining).Length + (2 * Environment.NewLine.Length);
            if (markdown.Length + rows[index].Length + reserve > maxLength)
            {
                markdown.AppendLine().AppendLine(TruncationNotice(remaining));
                break;
            }

            markdown.Append(rows[index]);
        }

        return markdown.ToString();
    }

    private static string TruncationNotice(int rows) =>
        $"{rows} more row(s) not shown because of the GitHub issue size limit; see the step summary of this run for the full table.";

    private async Task WriteSummaryAsync(string markdown, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(summaryPath))
        {
            return;
        }

        try
        {
            await File.AppendAllTextAsync(summaryPath, markdown, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The summary is a convenience; the exit code and stdout already carry the result.
            error.WriteLine($"Could not write the step summary: {ex.Message}");
        }
    }

    // Unlike the summary, the report is the input of the issue update: failing to write it must not look like a verdict.
    private async Task<bool> TryWriteReportAsync(string reportPath, string markdown, CancellationToken ct)
    {
        try
        {
            await File.WriteAllTextAsync(reportPath, markdown, ct);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Could not write the report: {ex.Message}");
            return false;
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
