using System.Globalization;
using System.Text.RegularExpressions;
using Trainer.TestSupport;

namespace Trainer.Web.Tests;

public sealed partial class ThemeTokenContrastTests
{
    [GeneratedRegex(@"--(?<name>[a-z-]+):\s*#(?<hex>[0-9a-fA-F]{6});", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    public static TheoryData<string, string, string, double> Pairs()
    {
        (string Foreground, string Background, double Minimum)[] pairs =
        [
            ("text", "bg", 4.5),
            ("muted", "bg", 4.5),
            ("faint", "bg", 4.5),
            ("faint", "panel", 4.5),
            ("faint", "side", 4.5),
            ("strong", "panel", 4.5),
            ("link", "bg", 4.5),
            ("on-ok", "ok", 4.5),
            ("ok", "bg", 3.0),
            ("bad", "bg", 3.0),
            ("warn", "bg", 3.0),
            ("accent", "side", 3.0),
            ("lv-junior", "bg", 4.5),
            ("lv-middle", "bg", 4.5),
            ("lv-senior", "bg", 4.5),
            ("lv-lead", "bg", 4.5),
            ("lv-architect", "bg", 4.5),
            // Question screen (islands.css): sidebar items, code lines, terminal feedback, input borders (non-text).
            ("muted", "side", 4.5),
            ("muted", "panel", 4.5),
            ("muted", "term", 4.5),
            ("text", "panel", 4.5),
            ("ok-t", "side", 4.5),
            ("ok-t", "term", 4.5),
            ("bad-t", "term", 4.5),
            ("warn", "term", 4.5),
            // Profile and import (islands.css, site.css): links, the import panel border and the progress meters on panels.
            ("link", "panel", 4.5),
            ("accent", "panel", 3.0),
            ("ok", "panel", 3.0),
        ];

        TheoryData<string, string, string, double> data = [];
        foreach (string theme in new[] { "dark", "light" })
        {
            foreach ((string foreground, string background, double minimum) in pairs)
            {
                data.Add(theme, foreground, background, minimum);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void ThemeTokens_MeetWcagAA(string theme, string foreground, string background, double minimum)
    {
        Dictionary<string, string> tokens = ReadTheme(theme);

        double ratio = ContrastRatio(tokens[foreground], tokens[background]);

        Assert.True(ratio >= minimum, $"{theme}: --{foreground} on --{background} is {ratio:0.00}:1, needs {minimum}:1");
    }

    private static Dictionary<string, string> ReadTheme(string theme)
    {
        string css = File.ReadAllText(Path.Combine(ContentWorkspace.RepositoryRoot(), "src", "Trainer.Web", "wwwroot", "css", "tokens.css"));
        string selector = $"[data-theme=\"{theme}\"]";
        int start = css.IndexOf(selector, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{selector} block not found");
        int open = css.IndexOf('{', start);
        int close = css.IndexOf('}', open);

        return Token().Matches(css[open..close]).ToDictionary(m => m.Groups["name"].Value, m => m.Groups["hex"].Value, StringComparer.Ordinal);
    }

    private static double ContrastRatio(string hexA, string hexB)
    {
        double a = Luminance(hexA);
        double b = Luminance(hexB);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(string hex)
    {
        double Channel(int offset)
        {
            double c = int.Parse(hex.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(0)) + (0.7152 * Channel(2)) + (0.0722 * Channel(4));
    }
}
