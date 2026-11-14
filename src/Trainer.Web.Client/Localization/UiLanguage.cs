using System.Globalization;

namespace Trainer.Web.Client.Localization;

/// <summary>
/// The page language (<c>ru</c>, <c>en</c>) of a culture, from its name. The client runs with invariant globalization
/// (ADR 0013): a culture keeps its name, which picks the satellite resources, but its language data is invariant.
/// </summary>
internal static class UiLanguage
{
    public static string Of(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        string name = culture.Name;
        int dash = name.IndexOf('-', StringComparison.Ordinal);
        return (dash < 0 ? name : name[..dash]).ToLowerInvariant();
    }

    /// <summary>The language of the current UI culture, fixed at start-up from the page's <c>&lt;html lang&gt;</c>.</summary>
    public static string Current => Of(CultureInfo.CurrentUICulture);
}
