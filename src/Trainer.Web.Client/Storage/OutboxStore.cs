using System.Text.Json;
using Trainer.Contracts;
using Trainer.Domain.Grading;
using Trainer.Domain.Ids;

namespace Trainer.Web.Client.Storage;

/// <summary>
/// The answer outboxes (spec 8.8, one key per owner, ADR 0008). Every write is a merge or removal by
/// <c>clientAnswerId</c> under Web Locks, never a whole-value write, so other tabs' entries survive. An answer that
/// could not be written stays in this tab's memory and is retried with the next write; nothing is ever evicted.
/// </summary>
public sealed class OutboxStore(IBrowserStore store) : IAsyncDisposable
{
    private const string AnswersMember = "answers";
    private const string IdMember = "clientAnswerId";

    private readonly Dictionary<string, KeyState> _keys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _watched = new(StringComparer.Ordinal);
    private readonly List<IAsyncDisposable> _watches = [];

    // No Web Locks, no localStorage or no outbox.js: writing would be an unlocked shared read-modify-write, so this tab
    // stops writing for its lifetime.
    private bool _memoryOnly;

    /// <summary>
    /// Called with the key when an append is refused for quota. Returning true says something else of this app's data was
    /// removed to make room (the static edition's progress checkpoint), and the append is tried once more; the answer log is
    /// the data that must not be lost, so it outranks anything derived from it.
    /// </summary>
    public Func<string, ValueTask<bool>>? MakeRoomAsync { get; set; }

    /// <summary>Raised after every change of the entries or of <see cref="Status"/>.</summary>
    public event Action? Changed;

    /// <summary>
    /// Raised with the key after this tab wrote (added, removed or moved) entries of it, and only then: unlike
    /// <see cref="Changed"/> it is never raised by a read or by a status change, so a cache of a key's content can drop
    /// itself on it without reacting to its own loading.
    /// </summary>
    public event Action<string>? KeyChanged;

    /// <summary>
    /// Raised, just before <see cref="KeyChanged"/>, when an <c>append</c> of this tab reached storage: the key, the answers
    /// and the script's result, whose <see cref="StoreResult.Previous"/> and <see cref="StoreResult.Fingerprint"/> say which
    /// log state the write started from and left. A cache of the log can then extend itself with these answers when it holds
    /// the state before, instead of reading the log again.
    /// </summary>
    public event Action<string, IReadOnlyList<AnswerDto>, StoreResult>? Appended;

    /// <summary>
    /// Raised when another tab changed a key this store watches (<see cref="WatchAsync"/>). Not raised for this tab's own
    /// writes: those raise <see cref="Changed"/>.
    /// </summary>
    public event Action? ChangedElsewhere;

    public StorageStatus Status
    {
        get
        {
            if (_memoryOnly)
            {
                return StorageStatus.MemoryOnly;
            }

            if (_keys.Values.Any(k => k.Condition == KeyCondition.NewerVersion))
            {
                return StorageStatus.NewerVersion;
            }

            if (_keys.Values.Any(k => k.Condition == KeyCondition.Unreadable))
            {
                return StorageStatus.MemoryOnly;
            }

            // A writable key keeps unsaved answers only after a write failed.
            if (_keys.Values.Any(k => k.Unsaved.Count > 0))
            {
                return StorageStatus.Full;
            }

            return _keys.Values.Any(k => k.RemovalFailed) ? StorageStatus.WriteFailed : StorageStatus.Persistent;
        }
    }

    /// <summary>
    /// Starts listening for other tabs' writes to <paramref name="key"/>, once per key. Call it before the first read, so a
    /// write between the read and the listener cannot be missed.
    /// </summary>
    public async ValueTask WatchAsync(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        // Marked before the await: a second caller while the script call is in flight must not register again.
        if (_watched.Add(key))
        {
            _watches.Add(await store.WatchKeyAsync(key, () => ChangedElsewhere?.Invoke()));
        }
    }

    public async ValueTask DisposeAsync()
    {
        IAsyncDisposable[] watches = [.. _watches];
        _watches.Clear();
        foreach (IAsyncDisposable watch in watches)
        {
            await watch.DisposeAsync();
        }
    }

    /// <summary>Answers held only in memory because storage did not take them.</summary>
    public int UnsavedCount => _keys.Values.Sum(k => k.Unsaved.Count);

    /// <summary>
    /// The pending answers of an outbox: the valid stored entries plus this tab's unsaved ones. Invalid stored entries
    /// (bad id, grade or mode, no <c>answeredAt</c>) are left in storage and never returned, so they are never sent.
    /// </summary>
    public async ValueTask<IReadOnlyList<AnswerDto>> ReadAsync(string key)
    {
        KeyState state = StateOf(key);
        StorageStatus before = Status;
        await RetryFailedRemovalAsync(key, state);
        await RefreshAsync(key, state);
        if (Status != before)
        {
            Changed?.Invoke();
        }

        return state.Snapshot();
    }

    public async ValueTask AddAsync(string key, AnswerDto answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        KeyState state = StateOf(key);
        state.Removed.Remove(answer.ClientAnswerId);
        state.Unsaved[answer.ClientAnswerId] = answer;
        StoreResult? stored = await PersistAsync(key, state);
        await RetryFailedRemovalAsync(key, state);
        Changed?.Invoke();
        ReportAppended(key, [answer], stored);
        KeyChanged?.Invoke(key);
    }

    /// <summary>
    /// Adds several answers in one append (a finished mock). Returns true only when every one of them reached storage;
    /// otherwise they stay in this tab's memory like any unsaved answer, and <see cref="Status"/> says why.
    /// </summary>
    public async ValueTask<bool> AddAllAsync(string key, IReadOnlyList<AnswerDto> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        KeyState state = StateOf(key);
        foreach (AnswerDto answer in answers)
        {
            state.Removed.Remove(answer.ClientAnswerId);
            state.Unsaved[answer.ClientAnswerId] = answer;
        }

        if (answers.Count > 0)
        {
            StoreResult? stored = await PersistAsync(key, state);
            await RetryFailedRemovalAsync(key, state);
            Changed?.Invoke();
            ReportAppended(key, answers, stored);
            KeyChanged?.Invoke(key);
        }

        return CanWrite(state) && !answers.Any(a => state.Unsaved.ContainsKey(a.ClientAnswerId));
    }

    /// <summary>Removes acknowledged answers; entries other tabs added meanwhile stay.</summary>
    public async ValueTask RemoveAsync(string key, IReadOnlyCollection<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        KeyState state = StateOf(key);
        foreach (string id in ids)
        {
            state.Unsaved.Remove(id);
            state.Removed.Add(id);
        }

        await RemoveStoredAsync(key, state);
        Changed?.Invoke();
        KeyChanged?.Invoke(key);
    }

    /// <summary>
    /// Moves every entry of <paramref name="fromKey"/> to <paramref name="toKey"/> (the anonymous import after sign-in),
    /// merging by id, destination first. An entry counts as moved only once it has left the source: when the script wrote
    /// the destination but could not rewrite the source, the entries are in both keys, the outcome reports them as
    /// <see cref="MoveOutcome.SourcePending"/>, and their removal from the source is retried now and by every later move
    /// from that key. <c>outbox.js</c> never moves an entry that another owner's outbox already holds; it only removes
    /// it from the source, so a half-finished move can never put one answer under two accounts.
    /// </summary>
    public async ValueTask<MoveOutcome> MoveAsync(string fromKey, string toKey)
    {
        if (string.Equals(fromKey, toKey, StringComparison.Ordinal))
        {
            return default;
        }

        KeyState source = StateOf(fromKey);
        KeyState target = StateOf(toKey);
        int moved = await RetryMoveCleanupAsync(fromKey, source);
        await RefreshAsync(fromKey, source);
        await RefreshAsync(toKey, target);

        moved += source.Unsaved.Count;
        foreach ((string id, AnswerDto answer) in source.Unsaved)
        {
            target.Removed.Remove(id);
            target.Unsaved[id] = answer;
        }

        source.Unsaved.Clear();
        // Sent even when the refresh saw an empty source: another tab may have appended since, and the script decides
        // under the locks.
        if (CanWrite(source) && CanWrite(target))
        {
            StoreResult result = await store.MutateAsync(StoreOp.Move(fromKey, toKey));
            if (result.Ok)
            {
                moved += result.Count;
            }
            else if (result.Persisted)
            {
                moved += await CleanUpPartialMoveAsync(fromKey, source, toKey, target);
            }
            else if (result.Error is StoreErrors.Corrupt or StoreErrors.NewerVersion)
            {
                // Another tab changed a key between the refresh and the move; classify both again.
                await RefreshAsync(fromKey, source);
                await RefreshAsync(toKey, target);
            }
            else
            {
                NoteGlobalError(result);
            }
        }

        await PersistAsync(toKey, target);
        Changed?.Invoke();
        KeyChanged?.Invoke(fromKey);
        KeyChanged?.Invoke(toKey);
        return new MoveOutcome(moved, source.MovePending.Count);
    }

    // The destination was written but the source was not: the ids found in both keys are the moved ones. They are hidden
    // from reads of the source (like any removal in flight) and their removal is tried once more right away.
    private async ValueTask<int> CleanUpPartialMoveAsync(string fromKey, KeyState source, string toKey, KeyState target)
    {
        await RefreshAsync(fromKey, source);
        await RefreshAsync(toKey, target);
        HashSet<string> inTarget = new(target.StoredIds, StringComparer.Ordinal);
        source.MovePending.UnionWith(source.StoredIds.Where(inTarget.Contains));
        return await RetryMoveCleanupAsync(fromKey, source);
    }

    // Returns how many pending entries left the source now.
    private async ValueTask<int> RetryMoveCleanupAsync(string fromKey, KeyState source)
    {
        if (source.MovePending.Count == 0)
        {
            return 0;
        }

        source.Removed.UnionWith(source.MovePending);
        await RemoveStoredAsync(fromKey, source);
        int cleaned = source.MovePending.RemoveWhere(id => !source.Removed.Contains(id));
        return cleaned;
    }

    private KeyState StateOf(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (!_keys.TryGetValue(key, out KeyState? state))
        {
            state = new KeyState();
            _keys[key] = state;
        }

        return state;
    }

    private bool CanWrite(KeyState state) => !_memoryOnly && state.Condition == KeyCondition.Writable;

    private async ValueTask RefreshAsync(string key, KeyState state)
    {
        StoredDocument document = _memoryOnly
            ? StoredDocument.Parse(await store.ReadAsync(key))
            : await store.ReadDocumentAsync(key);
        state.Condition = document.Kind switch
        {
            StoredKind.NewerVersion => KeyCondition.NewerVersion,
            StoredKind.Corrupt => KeyCondition.Unreadable,
            _ => KeyCondition.Writable,
        };
        state.Stored = [.. document.Items(AnswersMember).Select(ReadEntry).OfType<AnswerDto>()];
        state.StoredIds = document.Ids(AnswersMember, IdMember);
    }

    // Only what this write carried is reported, and only when it was the whole of it: answers that were unsaved from an
    // earlier failed write went along with it, so the count the script added is not the count the caller handed in.
    private void ReportAppended(string key, IReadOnlyList<AnswerDto> answers, StoreResult? stored)
    {
        if (stored is { Ok: true } && stored.Count == answers.Count)
        {
            Appended?.Invoke(key, answers, stored);
        }
    }

    private async ValueTask<StoreResult?> PersistAsync(string key, KeyState state)
    {
        if (!CanWrite(state) || state.Unsaved.Count == 0)
        {
            return null;
        }

        IReadOnlyList<AnswerDto> unsaved = [.. state.Unsaved.Values];
        JsonElement entries = JsonSerializer.SerializeToElement(unsaved, TrainerJsonContext.Default.IReadOnlyListAnswerDto);
        StoreOp append = StoreOp.Append(key, entries);
        StoreResult result = await MutateAsync(state, append);
        if (string.Equals(result.Error, StoreErrors.Quota, StringComparison.Ordinal) && MakeRoomAsync is { } makeRoom && await makeRoom(key))
        {
            result = await MutateAsync(state, append);
        }

        if (!result.Ok)
        {
            return result;
        }

        // Only what this append carried is saved: an answer added (or replaced) while it was in flight stays unsaved.
        foreach (AnswerDto sent in unsaved)
        {
            if (state.Unsaved.TryGetValue(sent.ClientAnswerId, out AnswerDto? current) && ReferenceEquals(current, sent))
            {
                state.Unsaved.Remove(sent.ClientAnswerId);
            }
        }

        return result;
    }

    private async ValueTask RemoveStoredAsync(string key, KeyState state)
    {
        if (!CanWrite(state) || state.Removed.Count == 0)
        {
            return;
        }

        string[] removing = [.. state.Removed];
        StoreResult result = await MutateAsync(state, StoreOp.Remove(key, removing));
        if (result.Ok)
        {
            // Ids removed while this op was in flight are still pending.
            state.Removed.ExceptWith(removing);
            state.RemovalFailed = false;
        }
        else
        {
            // The ids stay hidden from reads; the next read or write of this key tries again, and Status says so meanwhile.
            state.RemovalFailed = true;
        }
    }

    private async ValueTask RetryFailedRemovalAsync(string key, KeyState state)
    {
        if (state.RemovalFailed)
        {
            await RemoveStoredAsync(key, state);
        }
    }

    private async ValueTask<StoreResult> MutateAsync(KeyState state, StoreOp op)
    {
        StoreResult result = await store.MutateWithQuarantineAsync(op);
        if (string.Equals(result.Error, StoreErrors.NewerVersion, StringComparison.Ordinal))
        {
            state.Condition = KeyCondition.NewerVersion;
        }
        else if (string.Equals(result.Error, StoreErrors.Corrupt, StringComparison.Ordinal))
        {
            state.Condition = KeyCondition.Unreadable;
        }
        else
        {
            NoteGlobalError(result);
        }

        return result;
    }

    private void NoteGlobalError(StoreResult result)
    {
        if (result.Error is StoreErrors.NoLocks or StoreErrors.NoStorage or StoreErrors.Unavailable)
        {
            _memoryOnly = true;
        }
    }

    private static AnswerDto? ReadEntry(JsonElement entry)
    {
        AnswerDto? answer;
        try
        {
            answer = entry.Deserialize(TrainerJsonContext.Default.AnswerDto);
        }
        catch (JsonException)
        {
            return null;
        }

        return answer is not null
            && UlidText.IsValid(answer.ClientAnswerId)
            && !string.IsNullOrEmpty(answer.QuestionId)
            && GradeKeys.TryParse(answer.Grade, out _)
            && AnswerModeKeys.TryParse(answer.Mode, out _)
            && answer.AnsweredAt is not null
                ? answer
                : null;
    }

    private enum KeyCondition
    {
        Writable,

        /// <summary>A newer app's document: never touched.</summary>
        NewerVersion,

        /// <summary>An unparseable value that could not be quarantined: left in place.</summary>
        Unreadable,
    }

    private sealed class KeyState
    {
        public KeyCondition Condition { get; set; }

        /// <summary>The valid entries of the last read.</summary>
        public IReadOnlyList<AnswerDto> Stored { get; set; } = [];

        /// <summary>Every entry id of the last read, valid or not.</summary>
        public List<string> StoredIds { get; set; } = [];

        public OrderedDictionary<string, AnswerDto> Unsaved { get; } = new(StringComparer.Ordinal);

        /// <summary>Removed ids not yet removed from storage; hidden from reads until then.</summary>
        public HashSet<string> Removed { get; } = new(StringComparer.Ordinal);

        /// <summary>The last removal did not reach storage; it is retried by the next read or write of the key.</summary>
        public bool RemovalFailed { get; set; }

        /// <summary>Ids a move already wrote to its destination whose removal from this source has not succeeded yet.</summary>
        public HashSet<string> MovePending { get; } = new(StringComparer.Ordinal);

        public List<AnswerDto> Snapshot()
        {
            List<AnswerDto> result = [];
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (AnswerDto answer in Stored)
            {
                if (!Removed.Contains(answer.ClientAnswerId) && !Unsaved.ContainsKey(answer.ClientAnswerId) && seen.Add(answer.ClientAnswerId))
                {
                    result.Add(answer);
                }
            }

            result.AddRange(Unsaved.Values.Where(a => !Removed.Contains(a.ClientAnswerId)));
            return result;
        }
    }
}
