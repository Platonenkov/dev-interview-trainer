using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Trainer.Web.Seo;

/// <summary>Turns import-time rendered HTML into a short plain-text snippet for meta descriptions.</summary>
public static partial class PlainText
{
    public const int DescriptionLength = 160;

    // Block-level elements and br separate words visually; inline elements do not, so "a<em>b</em>c" stays "abc".
    [GeneratedRegex(
        @"</?(?:p|div|br|hr|li|ul|ol|dl|dt|dd|h[1-6]|pre|blockquote|table|thead|tbody|tfoot|tr|td|th|section|article|aside|header|footer|figure|figcaption)\b[^>]*>",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BlockTags();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    /// <summary>
    /// Strips tags, decodes entities, collapses whitespace and cuts at a word boundary within
    /// <paramref name="maxLength"/> UTF-16 code units, appending an ellipsis when something was cut.
    /// The cut never lands inside a surrogate pair or a grapheme cluster.
    /// </summary>
    public static string Snippet(string html, int maxLength = DescriptionLength)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);
        string withoutBlocks = BlockTags().Replace(html, " ");
        string text = Whitespace().Replace(WebUtility.HtmlDecode(Tags().Replace(withoutBlocks, string.Empty)), " ").Trim();
        if (text.Length <= maxLength)
        {
            return text;
        }

        HashSet<int> boundaries = TextElementBoundaries(text);
        int hardCut = maxLength - 1;
        while (hardCut > 0 && !boundaries.Contains(hardCut))
        {
            hardCut--;
        }

        int cut = text.LastIndexOf(' ', Math.Max(hardCut - 1, 0));
        if (cut < maxLength / 2 || !boundaries.Contains(cut))
        {
            cut = hardCut;
        }

        return text[..cut].TrimEnd(',', ';', ':', '.', '-') + "…";
    }

    private static HashSet<int> TextElementBoundaries(string text)
    {
        HashSet<int> boundaries = [text.Length];
        foreach (int start in StringInfo.ParseCombiningCharacters(text))
        {
            boundaries.Add(start);
        }

        return boundaries;
    }
}
