using System.Security.Cryptography;
using System.Text;

namespace Trainer.Web.Startup;

/// <summary>
/// The inline script that sets <c>data-theme</c> before the first paint (no flash of the wrong theme). The layout renders
/// <see cref="Source"/> verbatim and the CSP allows exactly its hash, so the two cannot drift apart. It also leaves
/// <c>window.ditApplyTheme</c> behind, so island pages can re-apply the theme after Blazor starts (islands-start.js).
/// </summary>
public static class ThemeScript
{
    private const string Head = "window.ditApplyTheme=function(){var t;try{t=localStorage.getItem('dit.theme');";

    // Carries a visitor's choice over from the key the server edition used before the namespace. Server edition only: the
    // static edition lives on the shared github.io origin, where a bare 'theme' key belongs to some other site, and it has
    // no earlier visitors to migrate. The old key is only read, never written or removed.
    private const string Migration =
        "if(t===null){t=localStorage.getItem('theme');if(t==='light'||t==='dark'){localStorage.setItem('dit.theme',t);}}";

    private const string Tail =
        "}catch(e){}" +
        "if(t!=='light'&&t!=='dark'){t=window.matchMedia('(prefers-color-scheme: light)').matches?'light':'dark';}" +
        "document.documentElement.dataset.theme=t;};ditApplyTheme();";

    /// <summary>The server edition's script.</summary>
    public const string Source = Head + Migration + Tail;

    /// <summary>The static edition's script: the same, without the migration.</summary>
    public const string StaticSource = Head + Tail;

    public static string For(SiteEdition edition) => edition == SiteEdition.Static ? StaticSource : Source;

    /// <summary>CSP source expressions for both scripts, computed once; the edition decides which one a page renders.</summary>
    public static readonly string CspSource = Hash(Source) + " " + Hash(StaticSource);

    private static string Hash(string script) =>
        "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script))) + "'";
}
