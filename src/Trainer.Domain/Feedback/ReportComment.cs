using System.Text;

namespace Trainer.Domain.Feedback;

/// <summary>The free-text part of a report. It is normalised once at the edge, so storage and the admin page see one shape.</summary>
public static class ReportComment
{
    /// <summary>Longest stored comment, in UTF-16 code units, after normalising.</summary>
    public const int MaxLength = 1000;

    /// <summary>
    /// Normalises line breaks to <c>\n</c>, drops control characters except <c>\n</c> and <c>\t</c>, and trims. Null,
    /// whitespace and text that is empty after that give <c>true</c> with a null comment.
    /// </summary>
    /// <returns>False when the normalised text is longer than <see cref="MaxLength"/>.</returns>
    public static bool TryNormalise(string? raw, out string? comment)
    {
        comment = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        StringBuilder builder = new(Math.Min(raw.Length, MaxLength + 1));
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '\r')
            {
                // CRLF counts as one break.
                if (i + 1 < raw.Length && raw[i + 1] == '\n')
                {
                    i++;
                }

                builder.Append('\n');
            }
            else if ((!char.IsControl(c) || c is '\n' or '\t') && !IsInvisibleFormatting(c))
            {
                builder.Append(c);
            }
        }

        string normalised = builder.ToString().Trim();
        if (normalised.Length > MaxLength)
        {
            return false;
        }

        comment = normalised.Length == 0 ? null : normalised;
        return true;
    }

    /// <summary>
    /// Zero-width characters and bidirectional overrides and isolates: invisible, so a comment could use them to make the
    /// admin page show text in a different order than it was written.
    /// </summary>
    private static bool IsInvisibleFormatting(char c) =>
        c is >= '​' and <= '‏'
            or >= '‪' and <= '‮'
            or >= '⁠' and <= '⁤'
            or >= '⁦' and <= '⁩'
            or '﻿';
}
