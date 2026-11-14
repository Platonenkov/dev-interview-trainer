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
    public const string Source =
        "window.ditApplyTheme=function(){var t;try{t=localStorage.getItem('theme');}catch(e){}" +
        "if(t!=='light'&&t!=='dark'){t=window.matchMedia('(prefers-color-scheme: light)').matches?'light':'dark';}" +
        "document.documentElement.dataset.theme=t;};ditApplyTheme();";

    /// <summary>CSP source expression for <see cref="Source"/>, computed once.</summary>
    public static readonly string CspSource =
        "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Source))) + "'";
}
