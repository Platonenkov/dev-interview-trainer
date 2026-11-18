namespace Trainer.Contracts;

/// <summary>
/// Builds the link of the static edition's "Report a problem": a new GitHub issue prefilled from an issue form. The
/// static edition has no server to receive a report, so the issue form carries the label (GitHub ignores
/// <c>labels=</c> in the URL for users without triage rights) and the URL only names the template and prefills fields.
/// </summary>
public static class ProblemReportLink
{
    public const string Template = "question-report.yml";

    /// <summary>The label the issue form applies; the URL never carries it.</summary>
    public const string Label = "content-report";

    /// <summary>GitHub answers a very long URL with an error page; this stays well below the ~8 KB limit.</summary>
    public const int MaxUrlLength = 6000;

    /// <summary>Characters of the issue title; a slug is unbounded, a title is not.</summary>
    public const int MaxTitleLength = 120;

    /// <summary>
    /// Returns <c>{issuesNewUrl}?template=..&amp;title=..&amp;question-id=..&amp;language=..&amp;page-url=..</c> with every value
    /// URL-encoded, so a value can never add or replace a parameter.
    /// </summary>
    /// <exception cref="ArgumentException">The encoded URL would exceed <see cref="MaxUrlLength"/>.</exception>
    public static string Build(string issuesNewUrl, string questionId, string slug, string culture, string pageUrl)
    {
        ArgumentException.ThrowIfNullOrEmpty(issuesNewUrl);
        ArgumentNullException.ThrowIfNull(questionId);
        ArgumentNullException.ThrowIfNull(slug);
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentNullException.ThrowIfNull(pageUrl);

        string title = TruncateTitle(CollapseWhitespace($"[{culture}] {slug}"));
        string url = $"{issuesNewUrl}?template={Uri.EscapeDataString(Template)}"
            + $"&title={Uri.EscapeDataString(title)}"
            + $"&question-id={Uri.EscapeDataString(questionId)}"
            + $"&language={Uri.EscapeDataString(culture)}"
            + $"&page-url={Uri.EscapeDataString(pageUrl)}";

        if (url.Length > MaxUrlLength)
        {
            throw new ArgumentException($"The report URL would be {url.Length} characters; the limit is {MaxUrlLength}.", nameof(pageUrl));
        }

        return url;
    }

    // A newline or control character in a prefilled title would break the single-line field; runs become one space.
    private static string CollapseWhitespace(string value)
    {
        System.Text.StringBuilder builder = new(value.Length);
        bool pendingSpace = false;
        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    // A cut between the halves of a surrogate pair would encode as a replacement character.
    private static string TruncateTitle(string title)
    {
        if (title.Length <= MaxTitleLength)
        {
            return title;
        }

        int length = char.IsHighSurrogate(title[MaxTitleLength - 1]) ? MaxTitleLength - 1 : MaxTitleLength;
        return title[..length].TrimEnd();
    }
}
