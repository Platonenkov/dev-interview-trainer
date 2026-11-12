using Trainer.Content.Model;
using Trainer.Content.Reading;
using Trainer.Content.Validation;

namespace Trainer.Snippets.Tests;

public class SnippetVerifierTests
{
    private sealed class FakeRunner(Func<int, SnippetResult> runs, SnippetResult? buildFailure = null) : ISnippetRunner
    {
        public int Builds { get; private set; }
        public int Runs { get; private set; }
        public List<string> Sources { get; } = [];

        public Task<SnippetBuild> BuildAsync(CodeBlock code, CancellationToken ct)
        {
            Builds++;
            Sources.Add(code.Source);
            return Task.FromResult(buildFailure is null ? SnippetBuild.Succeeded("unused") : SnippetBuild.Failed(buildFailure));
        }

        public Task<SnippetResult> RunAsync(SnippetBuild build, CancellationToken ct) => Task.FromResult(runs(Runs++));
    }

    private static SnippetResult Out(string value) => new("output", value);

    private static SourcedQuestion Question(
        string type = "output",
        Expectation? expect = null,
        IReadOnlyList<string>? accepted = null,
        IReadOnlyList<OptionDocument>? options = null,
        string? fixedSource = null,
        Expectation? fixedExpect = null,
        string harness = "statements") =>
        new("dotnet/csharp/q.yaml", new QuestionDocument
        {
            Id = "01KBZ3Q8N9V2K3M4P5R6S7T8WA",
            Slug = "q",
            Stack = "dotnet",
            Topic = "csharp",
            Level = "junior",
            Type = type,
            Grading = "auto",
            Tags = [],
            Code = new CodeBlock { Language = "csharp", Harness = harness, Source = "Console.Write(1);" },
            Expect = expect,
            AcceptedOutputs = accepted,
            Options = options,
            FixedSource = fixedSource,
            FixedExpect = fixedExpect,
            Links = [],
            Locales = new Dictionary<string, QuestionLocaleDocument>(),
            Meta = new QuestionMeta { Authors = [], Verified = new DateOnly(2026, 11, 1) },
        });

    private static ContentSet Set(params SourcedQuestion[] questions) =>
        new(1, [], new Dictionary<string, IReadOnlyList<TopicDocument>>(), questions,
            new Dictionary<string, IReadOnlyList<RedirectDocument>>(), "hash", []);

    private static Expectation Expect(string kind, string? value = null) => new() { Kind = kind, Value = value };

    private static OptionDocument Option(string id, bool correct, Expectation? expect) => new() { Id = id, Correct = correct, Expect = expect };

    private static async Task<IReadOnlyList<ContentIssue>> VerifyAsync(FakeRunner runner, params SourcedQuestion[] questions) =>
        await new SnippetVerifier(runner).VerifyAsync(Set(questions), CancellationToken.None);

    [Fact]
    public async Task MatchingExpectation_ProducesNoIssues()
    {
        FakeRunner runner = new(_ => Out("333"));
        IReadOnlyList<ContentIssue> issues = await VerifyAsync(runner, Question(expect: Expect("output", "333"), accepted: ["333"]));
        Assert.Empty(issues);
        Assert.Equal(1, runner.Builds);
        Assert.Equal(1, runner.Runs);
    }

    [Fact]
    public async Task OutputDiffersFromExpect_IsMismatch()
    {
        IReadOnlyList<ContentIssue> issues = await VerifyAsync(new FakeRunner(_ => Out("012")), Question(expect: Expect("output", "333")));
        ContentIssue issue = Assert.Single(issues);
        Assert.Equal("snippet-mismatch", issue.Code);
        Assert.Equal("dotnet/csharp/q.yaml", issue.RelativePath);
        Assert.Equal("expect", issue.JsonPath);
    }

    [Fact]
    public async Task ExceptionExpectation_MatchesTypeName()
    {
        IReadOnlyList<ContentIssue> issues = await VerifyAsync(
            new FakeRunner(_ => new SnippetResult("exception", "System.InvalidOperationException")),
            Question(expect: Expect("exception", "System.InvalidOperationException")));
        Assert.Empty(issues);
    }

    [Fact]
    public async Task CompileErrorExpectation_MatchesBuildFailure()
    {
        FakeRunner runner = new(_ => Out("never"), new SnippetResult("compile-error", "CS0103"));
        IReadOnlyList<ContentIssue> issues = await VerifyAsync(runner, Question(expect: Expect("compile-error")));
        Assert.Empty(issues);
        Assert.Equal(0, runner.Runs);
    }

    [Fact]
    public async Task UnexpectedCompileError_IsMismatch()
    {
        FakeRunner runner = new(_ => Out("never"), new SnippetResult("compile-error", "CS0103"));
        IReadOnlyList<ContentIssue> issues = await VerifyAsync(runner, Question(expect: Expect("output", "1")));
        Assert.Equal("snippet-mismatch", Assert.Single(issues).Code);
    }

    [Fact]
    public async Task WrongOptionMarkedCorrect_IsOptionMismatch()
    {
        OptionDocument[] options =
        [
            Option("a", true, Expect("output", "012")),
            Option("b", false, Expect("output", "333")),
        ];
        IReadOnlyList<ContentIssue> issues = await VerifyAsync(new FakeRunner(_ => Out("333")), Question(options: options));
        Assert.Equal(["snippet-option-mismatch", "snippet-option-mismatch"], issues.Select(i => i.Code));
        Assert.Equal(["options[0].expect", "options[1].expect"], issues.Select(i => i.JsonPath));
    }

    [Fact]
    public async Task CorrectOptionsMatchingActual_ProduceNoIssues()
    {
        OptionDocument[] options =
        [
            Option("a", false, Expect("output", "012")),
            Option("b", true, Expect("output", "333")),
            Option("c", false, null),
        ];
        Assert.Empty(await VerifyAsync(new FakeRunner(_ => Out("333")), Question(options: options)));
    }

    [Fact]
    public async Task ActualNotInAcceptedOutputs_IsAcceptedMismatch()
    {
        IReadOnlyList<ContentIssue> issues = await VerifyAsync(
            new FakeRunner(_ => Out("42")), Question(expect: Expect("output", "42"), accepted: ["41"]));
        Assert.Equal("snippet-accepted-mismatch", Assert.Single(issues).Code);
    }

    [Fact]
    public async Task FixedSourceWrongResult_IsFixedMismatchAndUsesFixedSource()
    {
        FakeRunner runner = new(_ => Out("999"));
        IReadOnlyList<ContentIssue> issues = await VerifyAsync(
            runner, Question("find-bug", fixedSource: "Console.Write(2);", fixedExpect: Expect("output", "1000")));
        ContentIssue issue = Assert.Single(issues);
        Assert.Equal("snippet-fixed-mismatch", issue.Code);
        Assert.Equal("fixedExpect", issue.JsonPath);
        Assert.Equal(["Console.Write(2);"], runner.Sources);
    }

    [Fact]
    public async Task Nondeterministic_BuildsOnceRunsFiveTimes()
    {
        FakeRunner runner = new(i => Out(i == 3 ? "998" : "1000"));
        IReadOnlyList<ContentIssue> issues = await VerifyAsync(runner, Question("find-bug", expect: Expect("nondeterministic")));
        Assert.Empty(issues);
        Assert.Equal(1, runner.Builds);
        Assert.Equal(5, runner.Runs);
    }

    [Fact]
    public async Task Nondeterministic_WithIdenticalRuns_IsNotNondeterministic()
    {
        IReadOnlyList<ContentIssue> issues = await VerifyAsync(
            new FakeRunner(_ => Out("1000")), Question("find-bug", expect: Expect("nondeterministic")));
        Assert.Equal("snippet-not-nondeterministic", Assert.Single(issues).Code);
    }

    [Fact]
    public async Task Timeout_IsTimeoutIssueWithoutMismatchNoise()
    {
        IReadOnlyList<ContentIssue> issues = await VerifyAsync(
            new FakeRunner(_ => new SnippetResult("timeout", null)), Question(expect: Expect("output", "1"), accepted: ["1"]));
        Assert.Equal("snippet-timeout", Assert.Single(issues).Code);
    }

    [Fact]
    public async Task SandboxErrorOnRun_IsSandboxErrorIssue()
    {
        IReadOnlyList<ContentIssue> issues = await VerifyAsync(
            new FakeRunner(_ => new SnippetResult("sandbox-error", "run exit code 137")), Question(expect: Expect("output", "1")));
        ContentIssue issue = Assert.Single(issues);
        Assert.Equal("snippet-sandbox-error", issue.Code);
        Assert.Contains("137", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SandboxErrorOnBuild_IsSandboxErrorIssue()
    {
        FakeRunner runner = new(_ => Out("never"), new SnippetResult("sandbox-error", "docker not running"));
        IReadOnlyList<ContentIssue> issues = await VerifyAsync(runner, Question(expect: Expect("output", "1")));
        Assert.Equal("snippet-sandbox-error", Assert.Single(issues).Code);
    }

    [Fact]
    public async Task NonRunnableQuestions_AreSkipped()
    {
        FakeRunner runner = new(_ => Out("x"));
        IReadOnlyList<ContentIssue> issues = await VerifyAsync(
            runner,
            Question(harness: "none", expect: Expect("output", "1")),
            Question("single", expect: Expect("output", "1")));
        Assert.Empty(issues);
        Assert.Equal(0, runner.Builds);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new SnippetVerifier(new FakeRunner(_ => Out("1"))).VerifyAsync(Set(Question(expect: Expect("output", "1"))), cts.Token));
    }
}
