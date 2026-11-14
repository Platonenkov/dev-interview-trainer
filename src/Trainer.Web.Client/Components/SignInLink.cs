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
        // Island pages have <base href="/">, so the base-relative path is the path itself.
        string returnPath = "/" + navigation.ToBaseRelativePath(navigation.Uri);
        return $"/{culture}/signin?returnUrl={Uri.EscapeDataString(returnPath)}";
    }
}
