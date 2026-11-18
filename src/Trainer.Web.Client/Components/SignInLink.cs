using Trainer.Web.Client.Localization;
using Microsoft.AspNetCore.Components;

namespace Trainer.Web.Client.Components;

/// <summary>The sign-in page of the current culture, returning to the current page afterwards.</summary>
internal static class SignInLink
{
    public static string For(NavigationManager navigation)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        string culture = UiLanguage.Current;
        // The server takes the return path without the site's path base, which is what the base-relative path is. The link
        // itself is base-relative too, resolved by the page's <base href>.
        string returnPath = "/" + navigation.ToBaseRelativePath(navigation.Uri);
        return $"{culture}/signin?returnUrl={Uri.EscapeDataString(returnPath)}";
    }
}
