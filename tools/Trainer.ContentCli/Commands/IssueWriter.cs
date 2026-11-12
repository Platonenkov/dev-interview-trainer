using Trainer.Content.Validation;

namespace Trainer.ContentCli.Commands;

internal sealed class IssueWriter(TextWriter output)
{
    /// <summary>Prints one line per issue as <c>path: code: message [pointer]</c>.</summary>
    public void Write(IEnumerable<ContentIssue> issues)
    {
        foreach (ContentIssue issue in issues)
        {
            output.WriteLine($"{issue.RelativePath}: {issue.Code}: {issue.Message} [{issue.JsonPath}]");
        }
    }
}
