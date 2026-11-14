using System.Globalization;
using System.Text;
using Microsoft.Extensions.Primitives;
using Trainer.Application.Catalog;
using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;

namespace Trainer.Web.Catalog;

/// <summary>
/// Turns the catalog query string into a normalised <see cref="CatalogQuery"/> and back. Every spelling of the same
/// selection (case, order, repeats, unknown values, tracking parameters) maps to one canonical query string, which is
/// what the canonical link and the output cache key are built from.
/// </summary>
public static class CatalogQueryParser
{
    public const string LevelParameter = "level";
    public const string TopicParameter = "topic";
    public const string TypeParameter = "type";
    public const string PageParameter = "page";
    public const string SearchParameter = "q";

    /// <summary>Longest search term kept; the search box carries the same <c>maxlength</c>.</summary>
    public const int MaxSearchLength = 100;

    public const int MaxPage = 1000;

    public static CatalogQuery Parse(
        CultureKey culture,
        StackKey stack,
        IQueryCollection query,
        IReadOnlySet<TopicKey> knownTopics)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(knownTopics);

        List<Level> levels = [];
        foreach (string? value in query[LevelParameter])
        {
            if (LevelKeys.TryParse(value, out Level level))
            {
                levels.Add(level);
            }
        }

        List<QuestionType> types = [];
        foreach (string? value in query[TypeParameter])
        {
            if (QuestionTypeKeys.TryParse(value, out QuestionType type))
            {
                types.Add(type);
            }
        }

        List<TopicKey> topics = [];
        foreach (string? value in query[TopicParameter])
        {
            foreach (TopicKey known in knownTopics)
            {
                if (string.Equals(known.Value, value, StringComparison.OrdinalIgnoreCase))
                {
                    topics.Add(known);
                    break;
                }
            }
        }

        return Normalise(new CatalogQuery(culture, stack, levels, topics, types, ParsePage(query[PageParameter]), ParseSearch(query)));
    }

    /// <summary>
    /// The search term of the request: the first <c>q</c>, with whitespace and control characters (a NUL would make
    /// PostgreSQL reject the parameter) collapsed to single spaces, trimmed and cut to <see cref="MaxSearchLength"/>
    /// characters without splitting a surrogate pair. Null when nothing is left.
    /// </summary>
    public static string? ParseSearch(IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(query);
        string? raw = query[SearchParameter].FirstOrDefault();
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        StringBuilder builder = new(Math.Min(raw.Length, MaxSearchLength));
        bool pendingSpace = false;
        foreach (char c in raw)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (builder.Length + (pendingSpace ? 2 : 1) > MaxSearchLength)
            {
                break;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        // A space is only ever written in front of a following character, so the cut cannot leave one at the end;
        // it can leave the high half of a surrogate pair.
        if (builder.Length > 0 && char.IsHighSurrogate(builder[^1]))
        {
            builder.Length--;
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>
    /// The query string without the leading <c>?</c>: dimensions in the fixed order level, topic, type, page, then the
    /// search term; values sorted; empty dimensions and <c>page=1</c> omitted. Empty when nothing is selected.
    /// </summary>
    public static string ToCanonicalQueryString(CatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        CatalogQuery normal = Normalise(query);

        StringBuilder builder = new();
        foreach (Level level in normal.Levels)
        {
            Append(builder, LevelParameter, LevelKeys.ToKey(level));
        }

        foreach (TopicKey topic in normal.Topics)
        {
            Append(builder, TopicParameter, topic.Value);
        }

        foreach (QuestionType type in normal.Types)
        {
            Append(builder, TypeParameter, QuestionTypeKeys.ToKey(type));
        }

        if (normal.Page > 1)
        {
            Append(builder, PageParameter, normal.Page.ToString(CultureInfo.InvariantCulture));
        }

        if (normal.Search is not null)
        {
            Append(builder, SearchParameter, normal.Search);
        }

        return builder.ToString();
    }

    /// <summary>The catalog path of the query's culture and stack followed by the canonical query string, if any.</summary>
    public static string ToUrl(CatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        string path = $"/{query.Culture.Value}/{query.Stack.Value}/questions";
        string queryString = ToCanonicalQueryString(query);
        return queryString.Length == 0 ? path : path + "?" + queryString;
    }

    private static CatalogQuery Normalise(CatalogQuery query) => query with
    {
        Levels = [.. query.Levels.Distinct().Order()],
        Topics = [.. query.Topics.Distinct().OrderBy(t => t.Value, StringComparer.Ordinal)],
        Types = [.. query.Types.Distinct().Order()],
        Page = Math.Clamp(query.Page, 1, MaxPage),
        Search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search,
    };

    private static int ParsePage(StringValues values)
    {
        string? first = values.FirstOrDefault();
        if (int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out int page))
        {
            return page;
        }

        // Digits that overflow int are still "beyond the last page", not junk.
        return !string.IsNullOrEmpty(first) && first.All(char.IsAsciiDigit) ? MaxPage : 1;
    }

    private static void Append(StringBuilder builder, string name, string value)
    {
        if (builder.Length > 0)
        {
            builder.Append('&');
        }

        builder.Append(name).Append('=').Append(Uri.EscapeDataString(value));
    }
}
