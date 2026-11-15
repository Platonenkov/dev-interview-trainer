using System.Text.Json;
using Trainer.Content.Reading;
using Trainer.ContentCli.Commands;

namespace Trainer.ContentCli.Tests;

/// <summary>Pins the JSON contract of <c>dump</c>, which the content wave driver uses as its only YAML parser.</summary>
public sealed class DumpCommandTests : IDisposable
{
    private readonly TempContentTree _tree = new();
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    public void Dispose() => _tree.Dispose();

    private DumpCommand Dump() => new(new ContentReader(), _output, _error);

    [Fact]
    public async Task Dump_ValidQuestion_EmitsDocumentAndSha()
    {
        _tree.AddQuestion("printer", "01KBZ3Q8N9V2K3M4P5R6S7T8W9", ["https://good.test/page"], null);

        int exitCode = await Dump().ExecuteAsync([_tree.Root], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Ok, exitCode);
        using JsonDocument json = JsonDocument.Parse(_output.ToString());
        JsonElement root = json.RootElement;
        Assert.False(string.IsNullOrEmpty(root.GetProperty("hash").GetString()));
        JsonElement question = root.GetProperty("questions")[0];
        Assert.Equal("dotnet/async/printer.yaml", question.GetProperty("path").GetString());
        string sha = question.GetProperty("sha256").GetString()!;
        Assert.Matches("^[0-9a-f]{64}$", sha);
        JsonElement document = question.GetProperty("document");
        Assert.Equal("printer", document.GetProperty("slug").GetString());
        Assert.Equal("2026-11-11", document.GetProperty("meta").GetProperty("verified").GetString());
        Assert.Equal("Заголовок", document.GetProperty("locales").GetProperty("ru").GetProperty("title").GetString());
        Assert.Contains("Заголовок", _output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, root.GetProperty("readIssues").GetArrayLength());
    }

    [Fact]
    public async Task Dump_BrokenYaml_ReportsReadIssueAndExitsZero()
    {
        File.WriteAllText(Path.Combine(_tree.Root, "dotnet", "async", "broken.yaml"), "id: [unclosed");

        int exitCode = await Dump().ExecuteAsync([_tree.Root], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Ok, exitCode);
        using JsonDocument json = JsonDocument.Parse(_output.ToString());
        JsonElement issue = json.RootElement.GetProperty("readIssues")[0];
        Assert.Equal("yaml-parse", issue.GetProperty("code").GetString());
        Assert.Equal("dotnet/async/broken.yaml", issue.GetProperty("path").GetString());
        Assert.False(string.IsNullOrEmpty(issue.GetProperty("message").GetString()));
        Assert.Equal(0, json.RootElement.GetProperty("questions").GetArrayLength());
    }

    [Fact]
    public async Task Dump_IncludesTopicsAndRedirects()
    {
        File.WriteAllText(Path.Combine(_tree.Root, "dotnet", "redirects.yaml"), "- { from: async/old-slug, to: async/new-slug }\n");

        int exitCode = await Dump().ExecuteAsync([_tree.Root], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Ok, exitCode);
        using JsonDocument json = JsonDocument.Parse(_output.ToString());
        JsonElement topic = json.RootElement.GetProperty("topics").GetProperty("dotnet")[0];
        Assert.Equal("async", topic.GetProperty("key").GetString());
        Assert.Equal("Tasks.", topic.GetProperty("locales").GetProperty("en").GetProperty("description").GetString());
        JsonElement redirect = json.RootElement.GetProperty("redirects").GetProperty("dotnet")[0];
        Assert.Equal("async/old-slug", redirect.GetProperty("from").GetString());
        Assert.Equal("async/new-slug", redirect.GetProperty("to").GetString());
    }

    [Fact]
    public async Task Dump_MissingRoot_ExitsUsageError()
    {
        int exitCode = await Dump().ExecuteAsync([Path.Combine(_tree.Root, "nope")], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, _output.ToString());
    }

    [Fact]
    public async Task Dump_TooManyArguments_ExitsUsageError()
    {
        int exitCode = await Dump().ExecuteAsync(["a", "b"], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.UsageError, exitCode);
    }
}
