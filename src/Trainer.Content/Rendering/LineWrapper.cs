using System.Text;

namespace Trainer.Content.Rendering;

/// <summary>
/// Splits highlighted HTML into lines so each line is a self-contained fragment: spans open at a line end
/// are closed and reopened on the next line, which keeps every line well-formed on its own.
/// </summary>
internal static class LineWrapper
{
    private const string SpanOpenPrefix = "<span";
    private const string SpanClose = "</span>";

    /// <summary>Returns one numbered <c>span.line</c> fragment per source line; each fragment is well-formed on its own.</summary>
    /// <param name="highlightedHtml">Encoded HTML containing only <c>span</c> elements and text.</param>
    public static IReadOnlyList<string> Split(string highlightedHtml)
    {
        List<string> openSpans = [];
        List<string> lines = [];
        StringBuilder line = new();
        int index = 0;

        while (index < highlightedHtml.Length)
        {
            char c = highlightedHtml[index];
            if (c == '\n')
            {
                FlushLine(lines, line, openSpans);
                index++;
            }
            else if (c == '<' && StartsAt(highlightedHtml, index, SpanOpenPrefix))
            {
                int end = highlightedHtml.IndexOf('>', index);
                if (end < 0)
                {
                    throw new InvalidOperationException("Unterminated span tag in highlighted code.");
                }

                string tag = highlightedHtml.Substring(index, end - index + 1);
                openSpans.Add(tag);
                line.Append(tag);
                index = end + 1;
            }
            else if (c == '<' && StartsAt(highlightedHtml, index, SpanClose))
            {
                if (openSpans.Count == 0)
                {
                    throw new InvalidOperationException("Unbalanced closing span in highlighted code.");
                }

                openSpans.RemoveAt(openSpans.Count - 1);
                line.Append(SpanClose);
                index += SpanClose.Length;
            }
            else if (c == '<')
            {
                throw new InvalidOperationException("Unexpected markup in highlighted code.");
            }
            else
            {
                line.Append(c);
                index++;
            }
        }

        FlushLine(lines, line, openSpans);

        // A trailing newline in the source must not produce an extra empty numbered line.
        if (lines.Count > 1 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        string[] wrapped = new string[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            wrapped[i] = $"<span class=\"line\" data-line=\"{i + 1}\">{lines[i]}{SpanClose}";
        }

        return wrapped;
    }

    private static void FlushLine(List<string> lines, StringBuilder line, List<string> openSpans)
    {
        for (int i = 0; i < openSpans.Count; i++)
        {
            line.Append(SpanClose);
        }

        lines.Add(line.ToString());
        line.Clear();
        foreach (string tag in openSpans)
        {
            line.Append(tag);
        }
    }

    private static bool StartsAt(string value, int index, string prefix) =>
        string.CompareOrdinal(value, index, prefix, 0, prefix.Length) == 0;
}
