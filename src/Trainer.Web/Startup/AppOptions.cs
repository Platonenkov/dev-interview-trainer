namespace Trainer.Web.Startup;

/// <summary>The <c>App</c> configuration section.</summary>
public sealed class AppOptions
{
    public const string SectionName = "App";

    /// <summary>Absolute origin used in canonical URLs and the sitemap, without a trailing slash.</summary>
    public string? PublicBaseUrl { get; init; }

    /// <summary>Directory with the content repository checkout; required for the startup import.</summary>
    public string? ContentRoot { get; init; }

    /// <summary>Stack shown on <c>/{culture}</c>.</summary>
    public string DefaultStack { get; init; } = "dotnet";

    /// <summary>Retry policy of the startup import.</summary>
    public StartupImportOptions StartupImport { get; init; } = new();

    /// <summary>Trusted reverse proxies whose <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> are honoured; loopback is always trusted.</summary>
    public ForwardedHeadersSettings ForwardedHeaders { get; init; } = new();

    /// <summary>Applies EF Core migrations at startup. Allowed only in the Development environment.</summary>
    public bool ApplyMigrationsOnStartup { get; init; }
}
