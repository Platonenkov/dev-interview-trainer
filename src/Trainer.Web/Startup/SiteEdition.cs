namespace Trainer.Web.Startup;

/// <summary>Which build of the site the process serves.</summary>
public enum SiteEdition
{
    /// <summary>The full site: PostgreSQL, sign-in, API and islands that sync.</summary>
    Server,

    /// <summary>The static export for GitHub Pages, produced from a running instance (ADR 0022).</summary>
    Static,
}
