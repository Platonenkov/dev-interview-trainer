using Trainer.Web.StaticSite;

namespace Trainer.Web.Startup;

/// <summary>The <c>App</c> configuration section.</summary>
public sealed class AppOptions
{
    public const string SectionName = "App";

    /// <summary>
    /// Absolute public URL used in canonical URLs and the sitemap. Its path, if any (for example <c>/dev-interview-trainer</c>),
    /// is the path base the site is served under; a trailing slash is ignored.
    /// </summary>
    public string? PublicBaseUrl { get; init; }

    /// <summary>
    /// <see cref="SiteEdition.Server"/> (default) is the full site; <see cref="SiteEdition.Static"/> is the build exported to
    /// GitHub Pages (ADR 0022) and is accepted only in StaticExport, Development and Testing (<see cref="EditionStartupCheck"/>).
    /// </summary>
    public SiteEdition Edition { get; init; } = SiteEdition.Server;

    /// <summary>Settings of the static edition (<c>App:StaticSite</c>); ignored by the server edition.</summary>
    public StaticSiteOptions StaticSite { get; init; } = new();

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

    /// <summary>
    /// A file whose existence puts the site into maintenance: writes and the sign-in callback answer 503 outside the
    /// <c>/internal</c> prefix of the internal port (<see cref="MaintenanceGateMiddleware"/>); nginx answers 503 to every
    /// request on top of that. Unset leaves the gate off.
    /// </summary>
    public string? MaintenanceFlagPath { get; init; }
}
