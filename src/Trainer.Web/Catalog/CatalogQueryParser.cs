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

        return Normalise(new CatalogQuery(culture, stack, levels, topics, types, ParsePage(query[PageParameter])));
    }

    /// <summary>
    /// The query string without the leading <c>?</c>: dimensions in the fixed order level, topic, type, page; values
    /// sorted; empty dimensions and <c>page=1</c> omitted. Empty when nothing is selected.
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
