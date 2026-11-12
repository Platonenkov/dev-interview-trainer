namespace Trainer.ContentCli.Commands;

internal interface ICliCommand
{
    string Name { get; }

    string Usage { get; }

    /// <summary>Runs the command with the arguments that follow the command name and returns the process exit code.</summary>
    Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken ct);
}
