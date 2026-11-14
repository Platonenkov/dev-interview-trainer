namespace Trainer.Contracts;

public sealed record TopicDto(string Key, string Title);

public sealed record MeDto(long TelegramUserId, int StreakDays, int DueNow, IReadOnlyList<TopicProgressDto> Topics);

/// <summary>
/// The practice filters a page opens with, taken from its query string by the server. Empty lists and a null
/// <paramref name="Status"/> mean the query did not set them, so the island may fall back to the filters it remembers.
/// </summary>
/// <param name="Status"><c>all</c>, <c>new</c> or <c>due</c>; null when the query has no valid status.</param>
/// <param name="FirstQuestionId">A question id from <c>first=</c>: the island starts at once with it first.</param>
public sealed record PracticeFiltersDto(
    IReadOnlyList<string> Levels,
    IReadOnlyList<string> Topics,
    IReadOnlyList<string> Types,
    string? Status,
    string? FirstQuestionId);

/// <summary>The wire values of the Practice status filter, shared by the page, the API and the island.</summary>
public static class PracticeStatusKeys
{
    public const string All = "all";
    public const string New = "new";
    public const string Due = "due";
}

/// <summary>What the server hands an island at startup, so the island needs no extra request before it renders.</summary>
/// <param name="Me">Null for an anonymous visitor.</param>
public sealed record IslandBootstrapDto(
    string Culture,
    string Stack,
    IReadOnlyList<TopicDto> Topics,
    IReadOnlyList<string> Levels,
    IReadOnlyList<string> Types,
    PracticeFiltersDto InitialFilters,
    MeDto? Me);
