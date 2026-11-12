using Trainer.Content;
using Trainer.Content.Model;
using Trainer.Content.Reading;
using Trainer.Content.Validation;

namespace Trainer.Snippets;

/// <summary>Stable issue codes of <see cref="SnippetVerifier"/>.</summary>
public static class SnippetIssueCodes
{
    public const string Mismatch = "snippet-mismatch";
    public const string OptionMismatch = "snippet-option-mismatch";
    public const string AcceptedMismatch = "snippet-accepted-mismatch";
    public const string FixedMismatch = "snippet-fixed-mismatch";
    public const string NotNondeterministic = "snippet-not-nondeterministic";
    public const string Timeout = "snippet-timeout";

    /// <summary>Infrastructure failure, not a content defect: CI must fail the job without blaming the question.</summary>
    public const string SandboxError = "snippet-sandbox-error";
}

/// <summary>Compiles and runs every runnable snippet of a content set and compares the real result with what the question declares.</summary>
public sealed class SnippetVerifier(ISnippetRunner runner)
{
    private const int NondeterminismRuns = 5;

    public async Task<IReadOnlyList<ContentIssue>> VerifyAsync(ContentSet set, CancellationToken ct)
    {
        List<ContentIssue> issues = [];
        foreach (SourcedQuestion question in set.Questions)
        {
            ct.ThrowIfCancellationRequested();
            await VerifyQuestionAsync(question, issues, ct);
        }

        return issues;
    }

    private async Task VerifyQuestionAsync(SourcedQuestion question, List<ContentIssue> issues, CancellationToken ct)
    {
        QuestionDocument doc = question.Document;
        if (doc.Type is not ("output" or "find-bug") || !IsRunnable(doc.Code))
        {
            return;
        }

        if (NeedsRun(doc))
        {
            await VerifyMainSourceAsync(question, doc.Code!, issues, ct);
        }

        if (doc.FixedSource is not null && doc.FixedExpect is not null)
        {
            await VerifyFixedSourceAsync(question, doc.Code!, issues, ct);
        }
    }

    private static bool IsRunnable(CodeBlock? code) =>
        code is not null
        && string.Equals(code.Language, "csharp", StringComparison.Ordinal)
        && !string.Equals(code.Harness, ContentVocabulary.HarnessNone, StringComparison.Ordinal);

    private static bool NeedsRun(QuestionDocument doc) =>
        doc.Expect is not null
        || doc.AcceptedOutputs is not null
        || (doc.Options?.Any(o => o.Expect is not null) ?? false);

    private async Task VerifyMainSourceAsync(SourcedQuestion question, CodeBlock code, List<ContentIssue> issues, CancellationToken ct)
    {
        QuestionDocument doc = question.Document;
        bool wantsNondeterminism = IsNondeterministic(doc.Expect);

        SnippetResult? actual = await ExecuteAsync(question, code, "expect", wantsNondeterminism, issues, ct);
        if (actual is null)
        {
            return;
        }

        if (doc.Expect is not null && !Matches(doc.Expect, actual))
        {
            string issueCode = wantsNondeterminism ? SnippetIssueCodes.NotNondeterministic : SnippetIssueCodes.Mismatch;
            issues.Add(new ContentIssue(question.RelativePath, issueCode, $"Declared {Describe(doc.Expect)}, actual {Describe(actual)}.", "expect"));
        }

        if (doc.AcceptedOutputs is not null
            && !(string.Equals(actual.Kind, SnippetResult.KindOutput, StringComparison.Ordinal)
                 && doc.AcceptedOutputs.Contains(actual.Value ?? string.Empty, StringComparer.Ordinal)))
        {
            issues.Add(new ContentIssue(question.RelativePath, SnippetIssueCodes.AcceptedMismatch,
                $"Actual result {Describe(actual)} is not in acceptedOutputs.", "acceptedOutputs"));
        }

        if (doc.Options is not null)
        {
            VerifyOptions(question, doc.Options, actual, issues);
        }
    }

    private static void VerifyOptions(SourcedQuestion question, IReadOnlyList<OptionDocument> options, SnippetResult actual, List<ContentIssue> issues)
    {
        for (int i = 0; i < options.Count; i++)
        {
            OptionDocument option = options[i];
            if (option.Expect is null)
            {
                continue;
            }

            bool matchesActual = Matches(option.Expect, actual);
            if (matchesActual != option.Correct)
            {
                string reason = option.Correct
                    ? $"Option '{option.Id}' is marked correct but declares {Describe(option.Expect)} while the actual result is {Describe(actual)}."
                    : $"Option '{option.Id}' is marked incorrect but declares the actual result {Describe(actual)}.";
                issues.Add(new ContentIssue(question.RelativePath, SnippetIssueCodes.OptionMismatch, reason, $"options[{i}].expect"));
            }
        }
    }

    private async Task VerifyFixedSourceAsync(SourcedQuestion question, CodeBlock code, List<ContentIssue> issues, CancellationToken ct)
    {
        QuestionDocument doc = question.Document;
        Expectation fixedExpect = doc.FixedExpect!;
        CodeBlock fixedCode = code with { Source = doc.FixedSource! };

        SnippetResult? actual = await ExecuteAsync(question, fixedCode, "fixedSource", IsNondeterministic(fixedExpect), issues, ct);
        if (actual is not null && !Matches(fixedExpect, actual))
        {
            issues.Add(new ContentIssue(question.RelativePath, SnippetIssueCodes.FixedMismatch,
                $"Declared {Describe(fixedExpect)}, actual {Describe(actual)}.", "fixedExpect"));
        }
    }

    /// <summary>
    /// Builds once and runs once, or <see cref="NondeterminismRuns"/> times when nondeterminism is declared. Returns null after
    /// reporting an infrastructure or timeout issue, because comparing against a failed run would only add misleading noise.
    /// When nondeterminism is declared and observed, the result is a synthetic <c>nondeterministic</c> kind.
    /// </summary>
    private async Task<SnippetResult?> ExecuteAsync(
        SourcedQuestion question, CodeBlock code, string jsonPath, bool nondeterminism, List<ContentIssue> issues, CancellationToken ct)
    {
        await using SnippetBuild build = await runner.BuildAsync(code, ct);
        if (build.Failure is not null)
        {
            if (build.Failure.Kind == SnippetResult.KindCompileError)
            {
                return build.Failure;
            }

            AddInfrastructureIssue(question, build.Failure, jsonPath, issues);
            return null;
        }

        int runs = nondeterminism ? NondeterminismRuns : 1;
        HashSet<SnippetResult> distinct = [];
        SnippetResult? first = null;
        for (int i = 0; i < runs; i++)
        {
            SnippetResult result = await runner.RunAsync(build, ct);
            if (result.Kind is SnippetResult.KindTimeout or SnippetResult.KindSandboxError)
            {
                AddInfrastructureIssue(question, result, jsonPath, issues);
                return null;
            }

            first ??= result;
            distinct.Add(result);
        }

        return nondeterminism && distinct.Count >= 2 ? new SnippetResult(SnippetResult.KindNondeterministic, null) : first;
    }

    private static void AddInfrastructureIssue(SourcedQuestion question, SnippetResult result, string jsonPath, List<ContentIssue> issues)
    {
        if (result.Kind == SnippetResult.KindTimeout)
        {
            issues.Add(new ContentIssue(question.RelativePath, SnippetIssueCodes.Timeout, "The snippet did not finish before the run deadline.", jsonPath));
            return;
        }

        issues.Add(new ContentIssue(question.RelativePath, SnippetIssueCodes.SandboxError, $"Sandbox failure: {result.Value}", jsonPath));
    }

    private static bool IsNondeterministic(Expectation? expectation) =>
        expectation is not null && string.Equals(expectation.Kind, SnippetResult.KindNondeterministic, StringComparison.Ordinal);

    private static bool Matches(Expectation expected, SnippetResult actual) =>
        string.Equals(expected.Kind, actual.Kind, StringComparison.Ordinal)
        && (expected.Value is null || string.Equals(expected.Value, actual.Value, StringComparison.Ordinal));

    private static string Describe(Expectation expectation) => Describe(expectation.Kind, expectation.Value);

    private static string Describe(SnippetResult result) => Describe(result.Kind, result.Value);

    private static string Describe(string kind, string? value) => value is null ? kind : $"{kind} '{value}'";
}
