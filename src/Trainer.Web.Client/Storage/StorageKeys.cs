using System.Globalization;

namespace Trainer.Web.Client.Storage;

/// <summary>
/// Every <c>localStorage</c> key the islands use. Outboxes and mock data are kept per owner (ADR 0008): one key for
/// anonymous answers and one per Telegram user id, so a shared browser never mixes accounts and each outbox key holds
/// exactly the spec 8.8 document.
/// </summary>
public static class StorageKeys
{
    public const string AnonymousOutbox = "dit.outbox.v1.anon";

    /// <summary>The replayed progress of the anonymous answer log (<c>ProgressCheckpoint</c>): derived data, rebuilt from the log whenever it does not match.</summary>
    public const string AnonymousProgressCheckpoint = "dit.progress.v1.anon";

    /// <summary>Where unparseable values are copied before their key is cleared: the prefix plus Unix milliseconds.</summary>
    public const string CorruptPrefix = "dit.outbox.v1.corrupt.";

    public const string Account = "dit.account";

    /// <summary>The language a visitor chose in the static edition; a preference, last writer wins.</summary>
    public const string LanguageChoice = "dit.lang";

    public const string ServerClock = "dit.clock.v1";

    public const string PracticeFilters = "dit.practice.v1.filters";

    /// <summary>The finished mocks of a visitor of the static edition, kept on this device only (<c>StoredMockHistory</c>).</summary>
    public const string AnonymousMockHistory = "dit.mock.v1.anon.history";

    /// <summary>The owner segment of the mock keys for a visitor who is not signed in.</summary>
    public const string AnonymousOwner = "anon";

    public static string UserOutbox(long telegramUserId) => $"dit.outbox.v1.u.{Id(telegramUserId)}";

    public static string PendingSummaries(long telegramUserId) => $"dit.mock.v1.pending.{Id(telegramUserId)}";

    public static string FailedSummaries(long telegramUserId) => $"dit.mock.v1.failed.{Id(telegramUserId)}";

    /// <param name="owner">The Telegram user id as text, or <see cref="AnonymousOwner"/>.</param>
    public static string MockQuestions(string owner, string sessionId) => $"dit.mock.v1.{owner}.{sessionId}.questions";

    /// <param name="owner">The Telegram user id as text, or <see cref="AnonymousOwner"/>.</param>
    public static string MockProgress(string owner) => $"dit.mock.v1.{owner}.active";

    private static string Id(long telegramUserId) => telegramUserId.ToString(CultureInfo.InvariantCulture);
}
