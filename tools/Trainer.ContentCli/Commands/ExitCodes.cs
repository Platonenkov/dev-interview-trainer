namespace Trainer.ContentCli.Commands;

internal static class ExitCodes
{
    public const int Ok = 0;
    public const int ContentIssues = 1;
    public const int UsageError = 2;

    /// <summary>Docker or the sandbox failed; the content itself was not judged. CI reports this separately from content defects.</summary>
    public const int SandboxError = 3;

    public const int Cancelled = 130;
}
