using Trainer.Content.Model;
using Trainer.Content.Reading;
using Trainer.Content.Rendering;
using Trainer.Content.Validation;
using Trainer.ContentCli.Commands;
using Trainer.Snippets;

namespace Trainer.ContentCli.Tests;

/// <summary>Pins the documented mapping of each command's outcomes to process exit codes.</summary>
public sealed class CliExitCodeTests : IDisposable
{
    private readonly TempContentTree _tree = new();
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    public void Dispose() => _tree.Dispose();

    private sealed class FakeRunner(SnippetResult result) : ISnippetRunner
    {
        public Task<SnippetBuild> BuildAsync(CodeBlock code, CancellationToken ct) => Task.FromResult(SnippetBuild.Succeeded("unused"));

        public Task<SnippetResult> RunAsync(SnippetBuild build, CancellationToken ct) => Task.FromResult(result);
    }

    private ValidateCommand Validate() =>
        new(new ContentReader(), new ContentValidator(new ContentRenderer(), TimeProvider.System), _output, _error);

    private RunSnippetsCommand RunSnippets(SnippetResult actual, Func<CancellationToken, Task>? prepare = null) =>
        new(new ContentReader(), prepare ?? (_ => Task.CompletedTask), new SnippetVerifier(new FakeRunner(actual)), _output, _error);

    private NewQuestionCommand NewQuestion() => new(new QuestionTemplateBuilder(TimeProvider.System), _output, _error);

    private void AddOutputQuestion()
    {
        string yaml = """
            id: 01KBZ3Q8N9V2K3M4P5R6S7T8W9
            slug: printer
            stack: dotnet
            topic: async
            level: junior
            type: output
            grading: auto
            tags: [test]
            code:
              language: csharp
              harness: statements
              source: Console.Write(1);
            expect: { kind: output, value: "1" }
            acceptedOutputs: ["1"]
            options:
              - { id: a, correct: true, expect: { kind: output, value: "1" } }
              - { id: b, correct: false, expect: { kind: output, value: "2" } }
            links:
              - url: https://good.test/page
                lang: en
                kind: docs
            locales:
              ru:
                title: Заголовок
                prompt: Вопрос
                options: { a: "1", b: "2" }
                optionExplanations: { b: Нет. }
                answer: "1"
                explanation: Объяснение
                linkTitles: [Заголовок ссылки]
              en:
                title: Title
                prompt: Question
                options: { a: "1", b: "2" }
                optionExplanations: { b: No. }
                answer: "1"
                explanation: Explanation
                linkTitles: [Link title]
            meta:
              authors: [tester]
              verified: 2026-11-11
            """;
        File.WriteAllText(Path.Combine(_tree.Root, "dotnet", "async", "printer.yaml"), yaml);
    }

    private void AddBrokenFile() => File.WriteAllText(Path.Combine(_tree.Root, "dotnet", "async", "broken.yaml"), "id: [unclosed");

    [Fact]
    public async Task Validate_ValidContent_ExitsOk()
    {
        _tree.AddQuestion("alive", "01KBZ3Q8N9V2K3M4P5R6S7T8W4", ["https://good.test/page"], null);

        int exitCode = await Validate().ExecuteAsync([_tree.Root], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Ok, exitCode);
    }

    [Fact]
    public async Task Validate_BrokenFile_ExitsWithContentIssues()
    {
        AddBrokenFile();

        int exitCode = await Validate().ExecuteAsync([_tree.Root], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.ContentIssues, exitCode);
    }

    [Fact]
    public async Task Validate_TooManyArguments_IsAUsageError()
    {
        int exitCode = await Validate().ExecuteAsync(["a", "b"], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.UsageError, exitCode);
    }

    [Fact]
    public async Task Validate_MissingRoot_IsAUsageError()
    {
        int exitCode = await Validate().ExecuteAsync([Path.Combine(_tree.Root, "nope")], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.UsageError, exitCode);
    }

    [Fact]
    public async Task RunSnippets_MatchingResult_ExitsOk()
    {
        AddOutputQuestion();

        int exitCode = await RunSnippets(new SnippetResult(SnippetResult.KindOutput, "1")).ExecuteAsync([_tree.Root], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Ok, exitCode);
    }

    [Fact]
    public async Task RunSnippets_MismatchingResult_ExitsWithContentIssues()
    {
        AddOutputQuestion();

        int exitCode = await RunSnippets(new SnippetResult(SnippetResult.KindOutput, "2")).ExecuteAsync([_tree.Root], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.ContentIssues, exitCode);
    }

    [Fact]
    public async Task RunSnippets_UnreadableContent_ExitsWithContentIssues()
    {
        AddBrokenFile();

        int exitCode = await RunSnippets(new SnippetResult(SnippetResult.KindOutput, "1")).ExecuteAsync([_tree.Root], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.ContentIssues, exitCode);
    }

    [Fact]
    public async Task RunSnippets_SandboxFailingToPrepare_ExitsWithSandboxError()
    {
        AddOutputQuestion();
        RunSnippetsCommand command = RunSnippets(
            new SnippetResult(SnippetResult.KindOutput, "1"), _ => throw new InvalidOperationException("docker is gone"));

        int exitCode = await command.ExecuteAsync([_tree.Root], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.SandboxError, exitCode);
    }

    [Fact]
    public async Task RunSnippets_SandboxErrorResult_ExitsWithSandboxError()
    {
        AddOutputQuestion();

        int exitCode = await RunSnippets(new SnippetResult(SnippetResult.KindSandboxError, "boom")).ExecuteAsync([_tree.Root], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.SandboxError, exitCode);
    }

    [Fact]
    public async Task RunSnippets_Cancelled_Exits130()
    {
        AddOutputQuestion();
        using CancellationTokenSource cancellation = new();
        RunSnippetsCommand command = RunSnippets(new SnippetResult(SnippetResult.KindOutput, "1"), ct =>
        {
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

        int exitCode = await command.ExecuteAsync([_tree.Root], cancellation.Token);

        Assert.Equal(ExitCodes.Cancelled, exitCode);
    }

    [Fact]
    public async Task RunSnippets_TooManyArguments_IsAUsageError()
    {
        int exitCode = await RunSnippets(new SnippetResult(SnippetResult.KindOutput, "1")).ExecuteAsync(["a", "b"], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.UsageError, exitCode);
    }

    [Fact]
    public async Task RunSnippets_MissingRoot_IsAUsageError()
    {
        int exitCode = await RunSnippets(new SnippetResult(SnippetResult.KindOutput, "1"))
            .ExecuteAsync([Path.Combine(_tree.Root, "nope")], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.UsageError, exitCode);
    }

    [Fact]
    public async Task NewQuestion_ValidArguments_ExitsOkAndWritesTheFile()
    {
        int exitCode = await NewQuestion().ExecuteAsync(["dotnet", "async", "fresh-one", "open", _tree.Root], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Ok, exitCode);
        Assert.True(File.Exists(Path.Combine(_tree.Root, "dotnet", "async", "fresh-one.yaml")));
    }

    [Theory]
    [InlineData("dotnet", "async", "x")]
    [InlineData("Bad Stack", "async", "x", "open")]
    [InlineData("dotnet", "async", "x", "not-a-type")]
    public async Task NewQuestion_BadArguments_AreUsageErrors(params string[] args)
    {
        int exitCode = await NewQuestion().ExecuteAsync(args, TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.UsageError, exitCode);
    }

    [Fact]
    public async Task NewQuestion_ExistingFile_IsAUsageError()
    {
        string[] args = ["dotnet", "async", "twice", "open", _tree.Root];
        Assert.Equal(ExitCodes.Ok, await NewQuestion().ExecuteAsync(args, TestContext.Current.CancellationToken));

        int exitCode = await NewQuestion().ExecuteAsync(args, TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.UsageError, exitCode);
    }

    [Fact]
    public async Task GenerateSchemas_ExitsOkAndWritesSchemas()
    {
        int exitCode = await new GenerateSchemasCommand(_output, _error).ExecuteAsync([_tree.Root], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Ok, exitCode);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(_tree.Root, "schema"), "*.schema.json"));
    }

    [Fact]
    public async Task GenerateSchemas_MissingRoot_IsAUsageError()
    {
        int exitCode = await new GenerateSchemasCommand(_output, _error)
            .ExecuteAsync([Path.Combine(_tree.Root, "nope")], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.UsageError, exitCode);
    }

    [Fact]
    public async Task GenerateSchemas_TooManyArguments_IsAUsageError()
    {
        int exitCode = await new GenerateSchemasCommand(_output, _error).ExecuteAsync(["a", "b"], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.UsageError, exitCode);
    }
}
