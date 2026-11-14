using System.Globalization;

namespace Trainer.Web.Client.Localization;

/// <summary>
/// Picks the resource key for a counted phrase: <c>{key}.One</c>, <c>{key}.Few</c> or <c>{key}.Many</c>. Russian uses
/// all three forms (1, 21 / 2-4, 22-24 / 5-20, 25); English uses One for 1 and Many otherwise. Both resource files
/// carry all three keys.
/// </summary>
public static class PluralForms
{
    public static string Key(string baseKey, int count, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return baseKey + "." + Form(Math.Abs(count), culture);
    }

    private static string Form(int count, CultureInfo culture)
    {
        // The name, not TwoLetterISOLanguageName: under invariant globalization the language data is invariant (ADR 0013).
        if (!string.Equals(UiLanguage.Of(culture), "ru", StringComparison.Ordinal))
        {
            return count == 1 ? "One" : "Many";
        }

        int lastTwo = count % 100;
        int last = count % 10;
        if (last == 1 && lastTwo != 11)
        {
            return "One";
        }

        return last is >= 2 and <= 4 && lastTwo is < 12 or > 14 ? "Few" : "Many";
    }
}
