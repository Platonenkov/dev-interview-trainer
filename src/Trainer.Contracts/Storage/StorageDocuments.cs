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

/// <summary>
/// The finished mocks of the static edition (<c>dit.mock.v1.anon.history</c>), newest first. Entries are merged by
/// <see cref="StoredMockHistoryItem.Id"/> and the list keeps only the newest <see cref="MaxItems"/> of each stack, so the
/// document stays a few kilobytes whatever the number of mocks.
/// </summary>
public sealed record StoredMockHistory(int Version, IReadOnlyList<StoredMockHistoryItem> Items)
{
    public const int CurrentVersion = 1;

    /// <summary>How many mocks are kept per stack.</summary>
    public const int MaxItems = 20;
}

/// <summary>A <see cref="MockHistoryDto"/> with the stack it was finished on, which the profile filters by.</summary>
public sealed record StoredMockHistoryItem(
    string Id,
    string Stack,
    string Level,
    int QuestionCount,
    DateTimeOffset FinishedAt,
    int Knew,
    int Partial,
    int DidntKnow,
    int Unanswered)
{
    public static StoredMockHistoryItem From(string stack, MockHistoryDto mock)
    {
        ArgumentNullException.ThrowIfNull(mock);
        return new StoredMockHistoryItem(
            mock.Id, stack, mock.Level, mock.QuestionCount, mock.FinishedAt, mock.Knew, mock.Partial, mock.DidntKnow, mock.Unanswered);
    }

    public MockHistoryDto ToDto() => new(Id, Level, QuestionCount, FinishedAt, Knew, Partial, DidntKnow, Unanswered);
}

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
