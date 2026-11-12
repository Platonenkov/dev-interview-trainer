using System.Net;
using Trainer.Content.Reading;
using Trainer.ContentCli.Commands;
using Trainer.ContentCli.Links;

namespace Trainer.ContentCli.Tests;

public sealed class CheckLinksCommandTests : IDisposable
{
    private readonly TempContentTree _tree = new();
    private readonly string _summaryPath = Path.Combine(Path.GetTempPath(), "summary-" + Guid.NewGuid().ToString("N") + ".md");
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    public void Dispose()
    {
        _tree.Dispose();
        File.Delete(_summaryPath);
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

    [Theory]
    [InlineData("--content")]
    [InlineData("somewhere")]
    [InlineData("--content", "a", "b")]
    [InlineData("--other", "a")]
    [InlineData("--content", "")]
    public async Task BadArguments_AreUsageErrors(params string[] args)
    {
        int exitCode = await CreateCommand([], summaryPath: null).ExecuteAsync(args, TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Contains("Usage: check-links [--content <dir>]", _error.ToString(), StringComparison.Ordinal);
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
