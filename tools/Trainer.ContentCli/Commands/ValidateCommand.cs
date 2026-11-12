using Trainer.Content.Reading;
using Trainer.Content.Validation;

namespace Trainer.ContentCli.Commands;

internal sealed class ValidateCommand(
    IContentReader reader, IContentValidator validator, TextWriter output, TextWriter error) : ICliCommand
{
    public string Name => "validate";

    public string Usage => "validate [root]";

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

        // The validator deliberately leaves read problems to the caller, so both lists are needed for a verdict.
        List<ContentIssue> issues = [.. set.ReadIssues, .. validator.Validate(set)];
        new IssueWriter(output).Write(issues);
        if (issues.Count > 0)
        {
            error.WriteLine($"{issues.Count} content issue(s) found.");
            return ExitCodes.ContentIssues;
        }

        output.WriteLine($"Content is valid: {set.Questions.Count} question(s), hash {set.Hash}.");
        return ExitCodes.Ok;
    }
}
