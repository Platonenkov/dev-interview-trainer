using System.Net;
using Trainer.Content.Reading;
using Trainer.ContentCli.Commands;
using Trainer.ContentCli.Links;

namespace Trainer.ContentCli.Tests;

public sealed class CheckLinksCommandTests : IDisposable
{
    private readonly TempContentTree _tree = new();
    private readonly string _summaryPath = Path.Combine(Path.GetTempPath(), "summary-" + Guid.NewGuid().ToString("N") + ".md");
    private readonly string _reportPath = Path.Combine(Path.GetTempPath(), "report-" + Guid.NewGuid().ToString("N") + ".md");
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    public void Dispose()
    {
        _tree.Dispose();
        File.Delete(_summaryPath);
        File.Delete(_reportPath);
    }

    private sealed class StubHandler(Dictionary<string, HttpStatusCode> statuses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpStatusCode status = statuses.GetValueOrDefault(request.RequestUri!.GetLeftPart(UriPartial.Path), HttpStatusCode.OK);
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private CheckLinksCommand CreateCommand(Dictionary<string, HttpStatusCode> statuses, string? summaryPath) =>
        new(
            new ContentReader(),
            new LinkCollector(),
            () => new HttpClient(new StubHandler(statuses)),
            new LinkCheckOptions { RetryDelay = TimeSpan.FromMilliseconds(10) },
            summaryPath,
            _output,
            _error);

    private void AddQuestions()
    {
        _tree.AddQuestion("alive", "01KBZ3Q8N9V2K3M4P5R6S7T8W4", ["https://good.test/page", "https://blocked.test/page"], null);
        _tree.AddQuestion("broken", "01KBZ3Q8N9V2K3M4P5R6S7T8W5", ["https://gone.test/page"], "https://good.test/page");
    }

    [Fact]
    public async Task DeadLink_FailsTheRunAndIsReportedWithItsQuestions()
    {
        AddQuestions();
        CheckLinksCommand command = CreateCommand(
            new Dictionary<string, HttpStatusCode> { ["https://gone.test/page"] = HttpStatusCode.NotFound, ["https://blocked.test/page"] = HttpStatusCode.Forbidden },
            _summaryPath);

        int exitCode = await command.ExecuteAsync(["--content", _tree.Root], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.ContentIssues, exitCode);
        string output = _output.ToString();
        Assert.Contains("dead: https://gone.test/page (404) cited by dotnet/async/broken.yaml", output, StringComparison.Ordinal);
        Assert.Contains("unknown: https://blocked.test/page (403) cited by dotnet/async/alive.yaml", output, StringComparison.Ordinal);
        Assert.DoesNotContain("good.test", output, StringComparison.Ordinal);
        Assert.Contains("Checked 3 URL(s): 1 ok, 1 dead, 1 unknown.", output, StringComparison.Ordinal);

        string summary = await File.ReadAllTextAsync(_summaryPath, TestContext.Current.CancellationToken);
        Assert.Contains("| Status | URL | Detail | Cited by |", summary, StringComparison.Ordinal);
        Assert.Contains("| dead | https://gone.test/page | 404 | dotnet/async/broken.yaml |", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyUnknownLinks_ExitZero()
    {
        AddQuestions();
        CheckLinksCommand command = CreateCommand(
            new Dictionary<string, HttpStatusCode> { ["https://blocked.test/page"] = HttpStatusCode.TooManyRequests }, summaryPath: null);

        int exitCode = await command.ExecuteAsync(["--content", _tree.Root], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Ok, exitCode);
        Assert.Contains("unknown: https://blocked.test/page (429)", _output.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(_summaryPath));
    }

    [Fact]
    public async Task AllOk_PrintsOnlyTheTotalsAndAppendsToTheSummary()
    {
        AddQuestions();
        await File.WriteAllTextAsync(_summaryPath, "existing\n", TestContext.Current.CancellationToken);
        CheckLinksCommand command = CreateCommand([], _summaryPath);

        int exitCode = await command.ExecuteAsync(["--content", _tree.Root], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Ok, exitCode);
        Assert.Equal("Checked 3 URL(s): 3 ok, 0 dead, 0 unknown." + Environment.NewLine, _output.ToString());
        string summary = await File.ReadAllTextAsync(_summaryPath, TestContext.Current.CancellationToken);
        Assert.StartsWith("existing\n", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("| Status |", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportOption_WritesTotalsAndTable()
    {
        AddQuestions();
        CheckLinksCommand command = CreateCommand(
            new Dictionary<string, HttpStatusCode> { ["https://gone.test/page"] = HttpStatusCode.NotFound, ["https://blocked.test/page"] = HttpStatusCode.Forbidden },
            _summaryPath);

        int exitCode = await command.ExecuteAsync(["--content", _tree.Root, "--report", _reportPath], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.ContentIssues, exitCode);
        string report = await File.ReadAllTextAsync(_reportPath, TestContext.Current.CancellationToken);
        Assert.StartsWith("## Link check", report, StringComparison.Ordinal);
        Assert.Contains("Checked 3 URL(s): 1 ok, 1 dead, 1 unknown.", report, StringComparison.Ordinal);
        Assert.Contains("| Status | URL | Detail | Cited by |", report, StringComparison.Ordinal);
        Assert.Contains("| dead | https://gone.test/page | 404 | dotnet/async/broken.yaml |", report, StringComparison.Ordinal);
        Assert.Contains("| unknown | https://blocked.test/page | 403 | dotnet/async/alive.yaml |", report, StringComparison.Ordinal);
        // The report file and the step summary come from one renderer and must never drift apart.
        Assert.Equal(report, await File.ReadAllTextAsync(_summaryPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RenderReport_OverTheLimit_KeepsTotalsAndFittingRowsThenCountsTheCutRows()
    {
        List<LinkCheckResult> problems = [.. Enumerable.Range(0, 50).Select(i => new LinkCheckResult($"https://gone.test/{i:D2}", LinkStatus.Dead, "404"))];
        Dictionary<string, IReadOnlyList<string>> citations = problems.ToDictionary(p => p.Url, _ => (IReadOnlyList<string>)["dotnet/async/a.yaml"]);

        string report = CheckLinksCommand.RenderReport("Checked 50 URL(s): 0 ok, 50 dead, 0 unknown.", problems, citations, maxLength: 1000);

        Assert.True(report.Length <= 1000, $"report is {report.Length} characters");
        Assert.Contains("Checked 50 URL(s)", report, StringComparison.Ordinal);
        Assert.Contains("https://gone.test/00", report, StringComparison.Ordinal);
        Assert.DoesNotContain("https://gone.test/49", report, StringComparison.Ordinal);
        int shown = report.Split("| dead |").Length - 1;
        Assert.InRange(shown, 1, 49);
        Assert.Contains($"{50 - shown} more row(s) not shown", report, StringComparison.Ordinal);
        Assert.Contains("step summary", report, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderReport_NeverExceedsTheLimit_ForAnyLimit()
    {
        List<LinkCheckResult> problems = [.. Enumerable.Range(0, 50).Select(i => new LinkCheckResult($"https://gone.test/{i:D2}", LinkStatus.Dead, "404"))];
        Dictionary<string, IReadOnlyList<string>> citations = problems.ToDictionary(p => p.Url, _ => (IReadOnlyList<string>)["dotnet/async/a.yaml"]);
        const string totals = "Checked 50 URL(s): 0 ok, 50 dead, 0 unknown.";
        int full = CheckLinksCommand.RenderReport(totals, problems, citations).Length;

        for (int max = 600; max <= full + 3; max++)
        {
            string report = CheckLinksCommand.RenderReport(totals, problems, citations, max);

            Assert.True(report.Length <= max, $"limit {max}: report is {report.Length} characters");
            Assert.Equal(max >= full, !report.Contains("not shown", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void RenderReport_AtTheGitHubLimit_StaysInsideItAndAnnouncesTheCutRows()
    {
        List<LinkCheckResult> problems = [.. Enumerable.Range(0, 1500).Select(i => new LinkCheckResult($"https://gone.test/{i:D4}", LinkStatus.Dead, "404"))];
        Dictionary<string, IReadOnlyList<string>> citations = problems.ToDictionary(p => p.Url, _ => (IReadOnlyList<string>)["dotnet/async/a.yaml"]);

        for (int max = 65530; max <= 65536; max++)
        {
            string report = CheckLinksCommand.RenderReport("Checked 1500 URL(s).", problems, citations, max);

            Assert.True(report.Length <= max, $"limit {max}: report is {report.Length} characters");
            Assert.Contains("more row(s) not shown", report, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RenderReport_UnderTheLimit_IsNotTruncated()
    {
        List<LinkCheckResult> problems = [new LinkCheckResult("https://gone.test/a", LinkStatus.Dead, "404")];
        Dictionary<string, IReadOnlyList<string>> citations = new() { ["https://gone.test/a"] = ["dotnet/async/a.yaml"] };

        string report = CheckLinksCommand.RenderReport("t", problems, citations, maxLength: 65536);

        Assert.DoesNotContain("not shown", report, StringComparison.Ordinal);
        Assert.Contains("| dead | https://gone.test/a | 404 |", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportOption_NoProblems_WritesTotalsOnly()
    {
        AddQuestions();

        int exitCode = await CreateCommand([], summaryPath: null)
            .ExecuteAsync(["--content", _tree.Root, "--report", _reportPath], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Ok, exitCode);
        Assert.Equal(
            "## Link check" + Environment.NewLine + Environment.NewLine + "Checked 3 URL(s): 3 ok, 0 dead, 0 unknown." + Environment.NewLine,
            await File.ReadAllTextAsync(_reportPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReportOption_ReplacesAnEarlierReport()
    {
        AddQuestions();
        await File.WriteAllTextAsync(_reportPath, "stale dead-link table\n", TestContext.Current.CancellationToken);

        await CreateCommand([], summaryPath: null).ExecuteAsync(["--content", _tree.Root, "--report", _reportPath], TestContext.Current.CancellationToken);

        Assert.DoesNotContain("stale", await File.ReadAllTextAsync(_reportPath, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportOption_EscapesCraftedUrls()
    {
        _tree.AddQuestion("crafted", "01KBZ3Q8N9V2K3M4P5R6S7T8W6", ["https://gone.test/a_b*c"], null);
        CheckLinksCommand command = CreateCommand(new Dictionary<string, HttpStatusCode> { ["https://gone.test/a_b*c"] = HttpStatusCode.NotFound }, summaryPath: null);

        await command.ExecuteAsync(["--content", _tree.Root, "--report", _reportPath], TestContext.Current.CancellationToken);

        string report = await File.ReadAllTextAsync(_reportPath, TestContext.Current.CancellationToken);
        Assert.Contains("| dead | https://gone.test/a\\_b\\*c | 404 |", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportOption_UnwritableReport_IsAnErrorNotAGreenRun()
    {
        AddQuestions();
        string unwritable = Path.Combine(_tree.Root, "no-such-directory", "report.md");

        int exitCode = await CreateCommand([], summaryPath: null).ExecuteAsync(["--content", _tree.Root, "--report", unwritable], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Contains("Could not write the report", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportOption_ContentProblems_WriteNoReport()
    {
        File.WriteAllText(Path.Combine(_tree.Root, "dotnet", "async", "broken.yaml"), "id: [unterminated");

        int exitCode = await CreateCommand([], summaryPath: null).ExecuteAsync(["--content", _tree.Root, "--report", _reportPath], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.ContentIssues, exitCode);
        Assert.False(File.Exists(_reportPath));
    }

    [Fact]
    public async Task OptionsInEitherOrder()
    {
        AddQuestions();
        CheckLinksCommand command = CreateCommand([], summaryPath: null);

        int reportFirst = await command.ExecuteAsync(["--report", _reportPath, "--content", _tree.Root], TestContext.Current.CancellationToken);
        string first = await File.ReadAllTextAsync(_reportPath, TestContext.Current.CancellationToken);
        File.Delete(_reportPath);
        int contentFirst = await command.ExecuteAsync(["--content", _tree.Root, "--report", _reportPath], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Ok, reportFirst);
        Assert.Equal(ExitCodes.Ok, contentFirst);
        Assert.Equal(first, await File.ReadAllTextAsync(_reportPath, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("--content")]
    [InlineData("somewhere")]
    [InlineData("--content", "a", "b")]
    [InlineData("--other", "a")]
    [InlineData("--content", "")]
    [InlineData("--report")]
    [InlineData("--report", "--content")]
    [InlineData("--content", "--report", "x")]
    [InlineData("--report", "")]
    [InlineData("--content", "a", "--report")]
    [InlineData("--report", "a", "--report", "b")]
    [InlineData("--content", "a", "--content", "b")]
    public async Task BadArguments_AreUsageErrors(params string[] args)
    {
        int exitCode = await CreateCommand([], summaryPath: null).ExecuteAsync(args, TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Contains("Usage: check-links [--content <dir>] [--report <file>]", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void EscapeCell_NeutralisesMarkdownAndHtml()
    {
        Assert.Equal("a\\|b \\`c\\` \\[d\\] &amp;e &lt;br&gt; f\\\\g", CheckLinksCommand.EscapeCell("a|b `c` [d] &e <br> f\\g"));
    }

    [Fact]
    public void EscapeCell_NeutralisesEmphasisAndStrikethroughMarkers() =>
        Assert.Equal("\\*a\\* \\_b\\_ \\~c\\~", CheckLinksCommand.EscapeCell("*a* _b_ ~c~"));

    [Theory]
    [InlineData("a\r\nb", "a b")]
    [InlineData("a\nb", "a b")]
    [InlineData("a\rb", "a b")]
    public void EscapeCell_ReplacesLineBreaksSoTheRowStaysOnOneLine(string text, string expected) =>
        Assert.Equal(expected, CheckLinksCommand.EscapeCell(text));

    [Fact]
    public async Task MissingContentDirectory_IsAUsageError()
    {
        string missing = Path.Combine(_tree.Root, "nope");

        int exitCode = await CreateCommand([], summaryPath: null).ExecuteAsync(["--content", missing], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.UsageError, exitCode);
    }

    [Fact]
    public async Task UnwritableSummary_DoesNotChangeTheVerdict()
    {
        AddQuestions();
        string unwritable = Path.Combine(_tree.Root, "no-such-directory", "summary.md");
        CheckLinksCommand command = CreateCommand([], unwritable);

        int exitCode = await command.ExecuteAsync(["--content", _tree.Root], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Ok, exitCode);
        Assert.Contains("Could not write the step summary", _error.ToString(), StringComparison.Ordinal);
    }
}
