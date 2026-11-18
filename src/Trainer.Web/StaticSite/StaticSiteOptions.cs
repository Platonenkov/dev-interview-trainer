namespace Trainer.Web.StaticSite;

/// <summary>The <c>App:StaticSite</c> configuration section: settings that only the static edition uses.</summary>
public sealed class StaticSiteOptions
{
    public const string DefaultRepositoryUrl = "https://github.com/Platonenkov/dev-interview-trainer";

    /// <summary>The project's GitHub repository, an absolute <c>https</c> URL; "Report a problem" opens an issue there.</summary>
    public string RepositoryUrl { get; init; } = DefaultRepositoryUrl;

    /// <summary>
    /// The value of <c>&lt;meta name="google-site-verification"&gt;</c>, rendered on the root chooser and each culture home page.
    /// A public value (repository variable <c>GOOGLE_SITE_VERIFICATION</c>); empty renders nothing.
    /// </summary>
    public string? GoogleSiteVerification { get; init; }

    /// <summary>The value of <c>&lt;meta name="yandex-verification"&gt;</c>, rendered like <see cref="GoogleSiteVerification"/> (<c>YANDEX_VERIFICATION</c>).</summary>
    public string? YandexVerification { get; init; }

    /// <summary>
    /// Identity of the deployment (<c>&lt;commit&gt;.&lt;run id&gt;.&lt;attempt&gt;</c>), rendered into every page as
    /// <c>&lt;meta name="dit-build"&gt;</c>. The assemblies stay byte-identical for identical source, so the identity cannot
    /// live there. Empty (a local run) renders nothing.
    /// </summary>
    public string? BuildId { get; init; }

    /// <summary>
    /// Whether the static edition ships the Brotli-compressed runtime (<c>_framework/*.br</c>) for the page to decode itself
    /// (ADR 0027). GitHub Pages serves <c>.wasm</c> gzip at best, which leaves the cold load above its budget. Off by default;
    /// the pages and the export read the same value, so the files and the loader that asks for them cannot disagree.
    /// </summary>
    public bool BrotliRuntime { get; init; }

    /// <summary>The repository's new-issue page. Derived from <see cref="RepositoryUrl"/>, so it is not a configuration key.</summary>
    public string IssuesNewUrl => RepositoryUrl.TrimEnd('/') + "/issues/new";

    /// <summary>Fails startup for a repository URL that is not a plain absolute <c>https</c> URL.</summary>
    public void EnsureValid()
    {
        if (!Uri.TryCreate(RepositoryUrl, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException(
                "App:StaticSite:RepositoryUrl must be an absolute https URL without credentials, a query or a fragment.");
        }
    }
}
