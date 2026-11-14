using System.Net;
using System.Text;
using Trainer.Contracts;

namespace Trainer.Web.Client.Sessions;

/// <summary>
/// How many columns the options take in the full layout (from 1024 px). The approved mockup shows short options (outputs
/// such as <c>012</c>) in one row of four; a sentence or a code block does not fit a quarter of the column, so such option
/// sets keep two columns. Below 1024 px the stylesheet decides alone (one column on phones, two on tablets).
/// </summary>
public static class OptionLayout
{
    /// <summary>The longest visible text, in characters, that still reads well in a quarter of the question column.</summary>
    public const int ShortTextLength = 24;

    private static readonly string[] BlockTags = ["<pre", "<ul", "<ol", "<table", "<blockquote"];

    /// <returns>Up to four when every option is short, otherwise at most two; never more than there are options.</returns>
    public static int Columns(IReadOnlyList<OptionDto> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Count == 0)
        {
            return 1;
        }

        int limit = options.All(o => IsShort(o.TextHtml)) ? 4 : 2;
        return Math.Min(options.Count, limit);
    }

    private static bool IsShort(string html)
    {
        if (BlockTags.Any(tag => html.Contains(tag, StringComparison.OrdinalIgnoreCase))
            || CountOccurrences(html, "<p") > 1)
        {
            return false;
        }

        return VisibleText(html).Length <= ShortTextLength;
    }

    private static int CountOccurrences(string html, string value)
    {
        int count = 0;
        for (int at = html.IndexOf(value, StringComparison.OrdinalIgnoreCase); at >= 0; at = html.IndexOf(value, at + value.Length, StringComparison.OrdinalIgnoreCase))
        {
            count++;
        }

        return count;
    }

    // The option HTML is sanitised at import, so dropping everything between angle brackets leaves the visible text.
    private static string VisibleText(string html)
    {
        StringBuilder text = new(html.Length);
        bool inTag = false;
        foreach (char c in html)
        {
            if (c == '<')
            {
                inTag = true;
            }
            else if (c == '>')
            {
                inTag = false;
            }
            else if (!inTag)
            {
                text.Append(c);
            }
        }

        return WebUtility.HtmlDecode(text.ToString()).Trim();
    }
}
