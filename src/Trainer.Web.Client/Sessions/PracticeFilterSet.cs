using Trainer.Contracts;
using Trainer.Contracts.Storage;

namespace Trainer.Web.Client.Sessions;

/// <summary>
/// The filters a Practice session starts with (spec 5.3). Values are keys; an empty list does not restrict. Every
/// instance holds only values the page offers: unknown keys are dropped, the count is clamped and <c>due</c> needs a
/// signed-in user.
/// </summary>
public sealed record PracticeFilterSet(
    IReadOnlyList<string> Levels,
    IReadOnlyList<string> Topics,
    IReadOnlyList<string> Types,
    string Status,
    int Count)
{
    public const string All = PracticeStatusKeys.All;
    public const string New = PracticeStatusKeys.New;
    public const string Due = PracticeStatusKeys.Due;

    /// <summary>The server's practice limits (<c>SessionLimits</c>); the API clamps to the same range.</summary>
    public const int DefaultCount = 20;
    public const int MinCount = 1;
    public const int MaxCount = 50;

    /// <summary>The most excluded ids one session request may carry.</summary>
    public const int MaxExcluded = 1000;

    /// <summary>
    /// The filters the island opens with: filters in the URL win as a whole (a link to one topic means that topic, not
    /// that topic plus remembered levels); without them the remembered ones; otherwise the defaults. A <c>first=</c>
    /// link counts as a URL selection, so "Practise this question" is followed by questions from the whole stack rather
    /// than by a remembered filter that might match nothing else. The count is never in the URL, so it is always the
    /// remembered one.
    /// </summary>
    public static PracticeFilterSet Resolve(IslandBootstrapDto bootstrap, StoredPracticeFilters? remembered)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        PracticeFiltersDto url = bootstrap.InitialFilters;
        bool fromUrl = url.Levels.Count > 0 || url.Topics.Count > 0 || url.Types.Count > 0 || url.Status is not null || url.FirstQuestionId is not null;
        int count = remembered?.Count ?? DefaultCount;
        PracticeFilterSet chosen = fromUrl
            ? new PracticeFilterSet(url.Levels, url.Topics, url.Types, url.Status ?? All, count)
            : remembered is not null
                ? new PracticeFilterSet(remembered.Levels ?? [], remembered.Topics ?? [], remembered.Types ?? [], remembered.Status ?? All, count)
                : new PracticeFilterSet([], [], [], All, count);
        return chosen.Normalise(bootstrap);
    }

    /// <summary>Keeps only what <paramref name="bootstrap"/> offers, in its order, and clamps the count.</summary>
    public PracticeFilterSet Normalise(IslandBootstrapDto bootstrap)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        bool signedIn = bootstrap.Me is not null;
        string status = Status switch
        {
            New => New,
            Due when signedIn => Due,
            _ => All,
        };
        return new PracticeFilterSet(
            Known(bootstrap.Levels, Levels),
            Known([.. bootstrap.Topics.Select(t => t.Key)], Topics),
            Known(bootstrap.Types, Types),
            status,
            Math.Clamp(Count, MinCount, MaxCount));
    }

    /// <summary>
    /// Whether the session depends on what the server knows of this user's answers (signed-in <c>new</c> and <c>due</c>),
    /// so pending answers must be flushed before it is fetched.
    /// </summary>
    public bool ReadsServerState(bool signedIn) => signedIn && !string.Equals(Status, All, StringComparison.Ordinal);

    public PracticeSessionRequest ToRequest(string culture, string stack, string? firstQuestionId, IReadOnlyList<string> exclude) =>
        new(culture, stack, Levels, Topics, Types, Status, Count, firstQuestionId, exclude);

    public StoredPracticeFilters ToStored() => new(StoredPracticeFilters.CurrentVersion, Levels, Topics, Types, Status, Count);

    /// <summary>
    /// The ids a session request leaves out: the questions just seen (a "New session"), then, for an anonymous
    /// <c>new</c> session, the questions answered in this browser, newest first; distinct and at most
    /// <see cref="MaxExcluded"/>.
    /// </summary>
    public static IReadOnlyList<string> Excluded(IEnumerable<string> justSeen, IReadOnlyList<AnswerDto>? anonymousOutbox)
    {
        ArgumentNullException.ThrowIfNull(justSeen);
        IEnumerable<string> answered = (anonymousOutbox ?? [])
            .OrderByDescending(a => a.AnsweredAt ?? DateTimeOffset.MinValue)
            .Select(a => a.QuestionId);
        return [.. justSeen.Concat(answered).Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).Take(MaxExcluded)];
    }

    private static string[] Known(IReadOnlyList<string> offered, IReadOnlyList<string> chosen)
    {
        HashSet<string> picked = new(chosen ?? [], StringComparer.Ordinal);
        return [.. offered.Where(picked.Contains)];
    }
}
