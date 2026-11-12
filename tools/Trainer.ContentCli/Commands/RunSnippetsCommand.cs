using Trainer.Content.Reading;
using Trainer.Content.Validation;
using Trainer.Snippets;

namespace Trainer.ContentCli.Commands;

internal sealed class RunSnippetsCommand(
    IContentReader reader, Func<CancellationToken, Task> prepareSandbox, SnippetVerifier verifier, TextWriter output, TextWriter error) : ICliCommand
{
    public string Name => "run-snippets";

    public string Usage => "run-snippets [root]";

    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        string? root = ContentRoot.Resolve(args);
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
            error.WriteLine("Content files could not be read; fix them before running snippets (see 'validate').");
            return ExitCodes.ContentIssues;
        }

        IReadOnlyList<ContentIssue> issues;
        try
        {
            // The image download must not count against the first build deadline, so it happens up front.
            await prepareSandbox(ct);
            issues = await verifier.VerifyAsync(set, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            error.WriteLine("Cancelled.");
            return ExitCodes.Cancelled;
        }
        catch (Exception ex)
        {
            error.WriteLine($"Snippet sandbox is unavailable (is Docker installed and running?): {ex.Message}");
            return ExitCodes.SandboxError;
        }

        new IssueWriter(output).Write(issues);
        if (issues.Any(i => string.Equals(i.Code, SnippetIssueCodes.SandboxError, StringComparison.Ordinal)))
        {
            error.WriteLine("Snippet sandbox failure: this is an infrastructure problem, not a content defect.");
            return ExitCodes.SandboxError;
        }

        if (issues.Count > 0)
        {
            error.WriteLine($"{issues.Count} snippet issue(s) found.");
            return ExitCodes.ContentIssues;
        }

        output.WriteLine("All runnable snippets match their declared results.");
        return ExitCodes.Ok;
    }
}
