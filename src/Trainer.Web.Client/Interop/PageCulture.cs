using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Trainer.Web.Client.Interop;

/// <summary>
/// Fixes the runtime's culture to the page language (<c>&lt;html lang&gt;</c>, spec 8.3) before the host starts. Under
/// invariant globalization (ADR 0013) the runtime ignores Blazor's <c>applicationCulture</c> and starts invariant, which
/// would select the neutral resources; a named culture without data keeps resource lookup working, and the host loads
/// that culture's satellite assembly when it starts.
/// </summary>
internal static partial class PageCulture
{
    public static void Apply()
    {
        if (!OperatingSystem.IsBrowser())
        {
            throw new PlatformNotSupportedException("The page culture is read from the browser.");
        }

        CultureInfo culture = CultureInfo.GetCultureInfo(PageLanguage());
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    [SupportedOSPlatform("browser")]
    [JSImport("globalThis.ditPageLanguage")]
    private static partial string PageLanguage();
}
