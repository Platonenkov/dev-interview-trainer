using Trainer.Contracts;
using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;

namespace Trainer.Web.Client.Sessions;

/// <summary>
/// The Practice and Mock filters in a page's query string, for the static edition, where the server cannot read it. The
/// same normalisation as the server's <c>CatalogQueryParser</c> (a parity test runs both): unknown values and other
/// parameters (<c>_r</c>, tracking) are dropped, repeats collapse, levels and types come in their domain order and topics
/// in key order, and topic keys match case-insensitively.
/// </summary>
public static class PracticeQueryString
{
    private const string LevelParameter = "level";
    private const string TopicParameter = "topic";
    private const string TypeParameter = "type";
    private const string StatusParameter = "status";
    private const string FirstParameter = "first";

    /// <param name="query">With or without the leading <c>?</c>; a fragment is ignored.</param>
    /// <param name="topics">The topics the page offers; a topic outside them is dropped.</param>
    public static PracticeFiltersDto Parse(string query, IReadOnlyList<TopicDto> topics)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(topics);

        List<KeyValuePair<string, string>> pairs = Pairs(query);

        List<Level> levels = [];
        List<QuestionType> types = [];
        List<string> picked = [];
        foreach ((string name, string value) in pairs)
        {
            if (string.Equals(name, LevelParameter, StringComparison.OrdinalIgnoreCase) && LevelKeys.TryParse(value, out Level level))
            {
                levels.Add(level);
            }
            else if (string.Equals(name, TypeParameter, StringComparison.OrdinalIgnoreCase) && QuestionTypeKeys.TryParse(value, out QuestionType type))
            {
                types.Add(type);
            }
            else if (string.Equals(name, TopicParameter, StringComparison.OrdinalIgnoreCase))
            {
                picked.AddRange(topics
                    .Where(t => string.Equals(t.Key, value, StringComparison.OrdinalIgnoreCase) && TopicKey.TryCreate(t.Key, out _))
                    .Select(t => t.Key)
                    .Take(1));
            }
        }

        return new PracticeFiltersDto(
            [.. levels.Distinct().Order().Select(LevelKeys.ToKey)],
            [.. picked.Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal)],
            [.. types.Distinct().Order().Select(QuestionTypeKeys.ToKey)],
            Status(First(pairs, StatusParameter)),
            QuestionId.TryCreate(First(pairs, FirstParameter), out QuestionId first) ? first.Value : null);
    }

    /// <summary>
    /// The bootstrap with the filters of the page's address: in the static edition only (<c>StaticSite</c> is set), where
    /// the exported page cannot carry them. A mock mixes question types and has no status or opening question, so only
    /// the level and topic filters carry over.
    /// </summary>
    public static IslandBootstrapDto Apply(IslandBootstrapDto bootstrap, string uri, bool forMock)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(uri);
        if (bootstrap.StaticSite is null)
        {
            return bootstrap;
        }

        int mark = uri.IndexOf('?', StringComparison.Ordinal);
        PracticeFiltersDto parsed = Parse(mark < 0 ? string.Empty : uri[(mark + 1)..], bootstrap.Topics);
        return bootstrap with { InitialFilters = forMock ? new PracticeFiltersDto(parsed.Levels, parsed.Topics, [], null, null) : parsed };
    }

    private static string? Status(string? value) => value switch
    {
        PracticeStatusKeys.All or PracticeStatusKeys.New or PracticeStatusKeys.Due => value,
        _ => null,
    };

    private static string? First(List<KeyValuePair<string, string>> pairs, string name) =>
        pairs.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static List<KeyValuePair<string, string>> Pairs(string query)
    {
        int fragment = query.IndexOf('#', StringComparison.Ordinal);
        string text = (fragment < 0 ? query : query[..fragment]).TrimStart('?');
        List<KeyValuePair<string, string>> pairs = [];
        foreach (string part in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = part.IndexOf('=', StringComparison.Ordinal);
            string name = equals < 0 ? part : part[..equals];
            string value = equals < 0 ? string.Empty : part[(equals + 1)..];
            pairs.Add(new KeyValuePair<string, string>(Decode(name), Decode(value)));
        }

        return pairs;
    }

    // '+' is a space in a query string, and Uri.UnescapeDataString does not know that.
    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
}
