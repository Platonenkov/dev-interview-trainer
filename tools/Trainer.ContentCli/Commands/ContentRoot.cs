namespace Trainer.ContentCli.Commands;

internal static class ContentRoot
{
    public const string DefaultPath = "content";

    /// <summary>Returns the optional single root argument or the default; null when more than one argument was given.</summary>
    public static string? Resolve(IReadOnlyList<string> args) => args.Count switch
    {
        0 => DefaultPath,
        1 => args[0],
        _ => null,
    };
}
