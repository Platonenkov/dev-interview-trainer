namespace Trainer.Contracts.Storage;

/// <summary>
/// The pending (<c>dit.mock.v1.pending.{id}</c>) or failed (<c>dit.mock.v1.failed.{id}</c>) mock summaries of one
/// signed-in user. Items are merged and removed by <see cref="StoredMockSummary.MockSessionId"/>, never rewritten whole.
/// </summary>
public sealed record MockSummaryList(int Version, IReadOnlyList<StoredMockSummary> Items)
{
    public const int CurrentVersion = 1;
}

/// <summary>A queued mock summary.</summary>
/// <param name="Reason">Why the server refused it (failed queue only); <c>null</c> while pending.</param>
public sealed record StoredMockSummary(string MockSessionId, MockSummaryDto Summary, string? Reason);

/// <summary>The questions of the active mock (<c>dit.mock.v1.{owner}.{sessionId}.questions</c>), written once at start.</summary>
public sealed record StoredMockQuestions(int Version, string SessionId, IReadOnlyList<QuestionDto> Questions)
{
    public const int CurrentVersion = 1;
}

/// <summary>
/// The Practice filters last used to start a session (<c>dit.practice.v1.filters</c>), a preference: the last writer wins.
/// </summary>
public sealed record StoredPracticeFilters(
    int Version,
    IReadOnlyList<string> Levels,
    IReadOnlyList<string> Topics,
    IReadOnlyList<string> Types,
    string Status,
    int Count)
{
    public const int CurrentVersion = 1;
}
