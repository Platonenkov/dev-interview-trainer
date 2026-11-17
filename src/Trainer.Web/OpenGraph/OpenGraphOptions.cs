namespace Trainer.Web.OpenGraph;

/// <summary>The <c>App:OpenGraph</c> configuration section.</summary>
public sealed class OpenGraphOptions
{
    public const string SectionName = "App:OpenGraph";

    /// <summary>
    /// Directory of the rendered PNG cache. Unset means a <c>dit-og</c> directory under the system temp path; Compose
    /// sets a volume. A missing or unwritable directory only disables caching.
    /// </summary>
    public string? CacheDirectory { get; init; }
}
