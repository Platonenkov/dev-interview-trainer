namespace Trainer.Web.Internal;

/// <summary>The <c>App:Internal</c> configuration section.</summary>
public sealed class InternalOptions
{
    public const string SectionName = "App:Internal";

    /// <summary>Configuration key of the bearer token of the import endpoint (environment variable <c>ContentImport__Token</c>).</summary>
    public const string TokenKey = "ContentImport:Token";

    /// <summary>The local port the internal endpoints answer on; 0 means they are not mapped at all.</summary>
    public int Port { get; init; }
}
