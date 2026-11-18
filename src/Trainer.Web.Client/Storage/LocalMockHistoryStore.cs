using System.Text.Json;
using Trainer.Contracts;
using Trainer.Contracts.Storage;
using Trainer.Domain.Ids;
using Trainer.Domain.Keys;
using Trainer.Domain.Levels;

namespace Trainer.Web.Client.Storage;

/// <summary>
/// The finished mocks of the static edition (<see cref="StorageKeys.AnonymousMockHistory"/>), kept on this device and
/// shown on the profile. The same discipline as the outbox: every write is one <c>historyAdd</c> op under the key's Web
/// Lock that merges by mock id and keeps the newest <see cref="StoredMockHistory.MaxItems"/> of each stack, so two tabs finishing
/// mocks at once keep both and the document stays a few kilobytes. A mock that storage does not take (no Web Locks, no
/// <c>localStorage</c>, quota) stays in this tab's memory: reads return it and the next add tries it again, and
/// <see cref="Status"/> says why so <c>StorageNotice</c> can. A document of a newer app is never touched; an unreadable
/// one is set aside like an outbox, or, when that fails, left in place and this tab stops writing it.
/// </summary>
public sealed class LocalMockHistoryStore(IBrowserStore store)
{
    private const string ItemsMember = "items";

    // Keyed by mock id; insertion order is the order they are retried in.
    private readonly OrderedDictionary<string, StoredMockHistoryItem> _held = new(StringComparer.Ordinal);

    // No Web Locks, no localStorage or no outbox.js: the same rule as OutboxStore, this tab stops writing for its lifetime.
    private bool _memoryOnly;
    private Condition _condition;

    /// <summary>
    /// Called with the key when a write is refused for quota, like <see cref="OutboxStore.MakeRoomAsync"/>. Returning true says
    /// something derived (the static edition's progress checkpoint) was removed to make room, and the write is tried once
    /// more: a finished mock outranks data that a replay of the log rebuilds.
    /// </summary>
    public Func<string, ValueTask<bool>>? MakeRoomAsync { get; set; }

    /// <summary>Raised when <see cref="Status"/> changes.</summary>
    public event Action? Changed;

    /// <summary>
    /// <see cref="StorageStatus.Persistent"/> while every mock reached storage; <c>MemoryOnly</c> when this tab cannot write
    /// the history; <c>NewerVersion</c> for a newer app's document; <c>Full</c> when a write failed and the mock is held.
    /// </summary>
    public StorageStatus Status
    {
        get
        {
            if (_memoryOnly || _condition == Condition.Unreadable)
            {
                return StorageStatus.MemoryOnly;
            }

            if (_condition == Condition.NewerVersion)
            {
                return StorageStatus.NewerVersion;
            }

            return _held.Count > 0 ? StorageStatus.Full : StorageStatus.Persistent;
        }
    }

    /// <summary>
    /// The meaning of a finished mock for the profile: counts summed over its topics. <paramref name="mockSessionId"/> is the
    /// id the history merges by, so handing the same mock over twice stores it once.
    /// </summary>
    public static MockHistoryDto ToHistory(string mockSessionId, MockSummaryDto summary)
    {
        ArgumentException.ThrowIfNullOrEmpty(mockSessionId);
        ArgumentNullException.ThrowIfNull(summary);
        return new MockHistoryDto(
            mockSessionId,
            summary.Level,
            summary.QuestionCount,
            summary.FinishedAt,
            summary.Topics.Sum(t => t.Knew),
            summary.Topics.Sum(t => t.Partial),
            summary.Topics.Sum(t => t.DidntKnow),
            summary.Topics.Sum(t => t.Unanswered));
    }

    /// <summary>
    /// Adds a finished mock of <paramref name="stack"/>. When storage does not take it (<see cref="StoreResult.Persisted"/> false) it is held in memory
    /// and still read; mocks held earlier are tried again after a write that succeeded.
    /// </summary>
    public async ValueTask<StoreResult> AddAsync(string stack, MockHistoryDto mock)
    {
        ArgumentException.ThrowIfNullOrEmpty(stack);
        ArgumentNullException.ThrowIfNull(mock);
        StoredMockHistoryItem item = StoredMockHistoryItem.From(stack, mock);
        StorageStatus before = Status;
        StoreResult result = await WriteAsync(item);
        if (result.Persisted)
        {
            _held.Remove(mock.Id);
            await RetryHeldAsync();
        }
        else
        {
            _held[mock.Id] = item;
        }

        RaiseIfChanged(before);
        return result;
    }

    /// <summary>
    /// The stored mocks of <paramref name="stack"/> plus the ones held in memory, newest first, at most
    /// <see cref="StoredMockHistory.MaxItems"/>. Entries without a valid stack are never returned.
    /// </summary>
    public async ValueTask<IReadOnlyList<MockHistoryDto>> ReadAsync(string stack)
    {
        ArgumentException.ThrowIfNullOrEmpty(stack);
        StorageStatus before = Status;
        // A tab that cannot write never quarantines either: setting a value aside is a write under a lock.
        StoredDocument document = _memoryOnly
            ? StoredDocument.Parse(await store.ReadAsync(StorageKeys.AnonymousMockHistory))
            : await store.ReadDocumentAsync(StorageKeys.AnonymousMockHistory);
        _condition = document.Kind switch
        {
            StoredKind.NewerVersion => Condition.NewerVersion,
            StoredKind.Corrupt => Condition.Unreadable,
            _ => Condition.Writable,
        };

        List<StoredMockHistoryItem> items = [.. document.Items(ItemsMember).Select(ReadItem).OfType<StoredMockHistoryItem>()];
        // The held copy wins: it is what this tab tried to write last.
        items.RemoveAll(i => _held.ContainsKey(i.Id));
        items.AddRange(_held.Values);
        RaiseIfChanged(before);
        return [.. items
            .Where(m => string.Equals(m.Stack, stack, StringComparison.Ordinal))
            .OrderByDescending(m => m.FinishedAt)
            .ThenByDescending(m => m.Id, StringComparer.Ordinal)
            .Take(StoredMockHistory.MaxItems)
            .Select(m => m.ToDto())];
    }

    private async ValueTask<StoreResult> WriteAsync(StoredMockHistoryItem mock)
    {
        if (_memoryOnly || _condition != Condition.Writable)
        {
            return StoreResult.Failed(_memoryOnly ? StoreErrors.Unavailable : ConditionError(_condition));
        }

        JsonElement item = JsonSerializer.SerializeToElement(mock, TrainerJsonContext.Default.StoredMockHistoryItem);
        StoreOp add = StoreOp.HistoryAdd(StorageKeys.AnonymousMockHistory, item);
        StoreResult result = await store.MutateWithQuarantineAsync(add);
        if (string.Equals(result.Error, StoreErrors.Quota, StringComparison.Ordinal)
            && MakeRoomAsync is { } makeRoom
            && await makeRoom(StorageKeys.AnonymousMockHistory))
        {
            result = await store.MutateWithQuarantineAsync(add);
        }

        switch (result.Error)
        {
            case StoreErrors.NewerVersion:
                _condition = Condition.NewerVersion;
                break;
            case StoreErrors.Corrupt:
                // The quarantine failed: the value stays where it is and is never overwritten.
                _condition = Condition.Unreadable;
                break;
            case StoreErrors.NoLocks or StoreErrors.NoStorage or StoreErrors.Unavailable:
                _memoryOnly = true;
                break;
        }

        return result;
    }

    private async ValueTask RetryHeldAsync()
    {
        foreach (StoredMockHistoryItem held in _held.Values.ToArray())
        {
            if (!(await WriteAsync(held)).Persisted)
            {
                return;
            }

            _held.Remove(held.Id);
        }
    }

    private void RaiseIfChanged(StorageStatus before)
    {
        if (Status != before)
        {
            Changed?.Invoke();
        }
    }

    private static string ConditionError(Condition condition) =>
        condition == Condition.NewerVersion ? StoreErrors.NewerVersion : StoreErrors.Corrupt;

    // The same checks the server's profile relies on: an entry the profile could not show is left in storage, never returned.
    private static StoredMockHistoryItem? ReadItem(JsonElement element)
    {
        StoredMockHistoryItem? item;
        try
        {
            item = element.Deserialize(TrainerJsonContext.Default.StoredMockHistoryItem);
        }
        catch (JsonException)
        {
            return null;
        }

        return item is not null
            && UlidText.IsValid(item.Id)
            && StackKey.TryCreate(item.Stack, out _)
            && LevelKeys.TryParse(item.Level, out _)
            && item.QuestionCount >= 0
            && item.Knew >= 0
            && item.Partial >= 0
            && item.DidntKnow >= 0
            && item.Unanswered >= 0
                ? item
                : null;
    }

    private enum Condition
    {
        Writable,

        /// <summary>A newer app's document: never touched.</summary>
        NewerVersion,

        /// <summary>An unparseable value that could not be quarantined: left in place.</summary>
        Unreadable,
    }
}
