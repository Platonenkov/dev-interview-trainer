using System.Globalization;
using System.Text.Json;
using Trainer.Contracts;
using Trainer.Web.Client.Storage;

namespace Trainer.Web.Client.Tests;

public sealed class OutboxStoreTests
{
    private const string Key = "dit.outbox.v1.u.42";
    private const string QuestionId = "01KBZ3Q8N9V2K3M4P5R6S7T8WA";
    private static readonly DateTimeOffset At = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private static string Id(int n) => "01KC" + n.ToString("D22", CultureInfo.InvariantCulture);

    private static AnswerDto Answer(int n) => new(Id(n), QuestionId, "knew", false, "practice", null, At.AddSeconds(n));

    private static string Document(params string[] entries) => $"{{\"version\":1,\"answers\":[{string.Join(',', entries)}]}}";

    private static string Entry(int n, string grade = "knew", string answeredAt = "\"2026-11-12T10:00:00+00:00\"") =>
        $"{{\"clientAnswerId\":\"{Id(n)}\",\"questionId\":\"{QuestionId}\",\"grade\":\"{grade}\",\"usedHint\":false,\"mode\":\"practice\",\"answeredAt\":{answeredAt}}}";

    private static string[] StoredIds(FakeBrowserStore fake, string key)
    {
        string? raw = fake.Raw(key);
        if (raw is null)
        {
            return [];
        }

        using JsonDocument doc = JsonDocument.Parse(raw);
        return [.. doc.RootElement.GetProperty("answers").EnumerateArray().Select(e => e.GetProperty("clientAnswerId").GetString()!)];
    }

    [Fact]
    public async Task Add_ThenRead_RoundTripsSpecFormat()
    {
        FakeBrowserStore fake = new();
        OutboxStore outbox = new(fake);
        AnswerDto answer = new(Id(1), QuestionId, "partial", true, "mock", Id(99), At);

        await outbox.AddAsync(Key, answer);

        using (JsonDocument doc = JsonDocument.Parse(fake.Raw(Key)!))
        {
            JsonElement root = doc.RootElement;
            Assert.Equal(["version", "answers"], root.EnumerateObject().Select(p => p.Name));
            Assert.Equal(1, root.GetProperty("version").GetInt32());
            JsonElement entry = Assert.Single(root.GetProperty("answers").EnumerateArray());
            Assert.Equal(
                ["clientAnswerId", "questionId", "grade", "usedHint", "mode", "mockSessionId", "answeredAt"],
                entry.EnumerateObject().Select(p => p.Name));
            Assert.Equal(Id(1), entry.GetProperty("clientAnswerId").GetString());
            Assert.Equal("partial", entry.GetProperty("grade").GetString());
            Assert.Equal(At, entry.GetProperty("answeredAt").GetDateTimeOffset());
        }

        IReadOnlyList<AnswerDto> read = await new OutboxStore(fake).ReadAsync(Key);
        Assert.Equal([answer], read);
        Assert.Equal(StorageStatus.Persistent, outbox.Status);
    }

    [Fact]
    public async Task Remove_OnlyRemovesGivenIds()
    {
        FakeBrowserStore fake = new();
        OutboxStore outbox = new(fake);
        await outbox.AddAsync(Key, Answer(1));
        await outbox.AddAsync(Key, Answer(2));
        await outbox.AddAsync(Key, Answer(3));

        await outbox.RemoveAsync(Key, [Id(2), Id(7)]);

        Assert.Equal([Id(1), Id(3)], StoredIds(fake, Key));
        Assert.Equal([Answer(1), Answer(3)], await outbox.ReadAsync(Key));
    }

    [Fact]
    public async Task Move_AnonymousToUser_DeduplicatesById()
    {
        FakeBrowserStore fake = new();
        fake.SetRaw(StorageKeys.AnonymousOutbox, Document(Entry(1), Entry(2)));
        fake.SetRaw(Key, Document(Entry(2), Entry(3)));
        OutboxStore outbox = new(fake);

        MoveOutcome moved = await outbox.MoveAsync(StorageKeys.AnonymousOutbox, Key);

        Assert.Equal(new MoveOutcome(2, 0), moved);
        Assert.Null(fake.Raw(StorageKeys.AnonymousOutbox));
        Assert.Equal([Id(2), Id(3), Id(1)], StoredIds(fake, Key));
        Assert.Empty(await outbox.ReadAsync(StorageKeys.AnonymousOutbox));
        Assert.Equal(3, (await outbox.ReadAsync(Key)).Count);
    }

    [Fact]
    public async Task Move_KeepsUnsavedEntriesOfTheSource()
    {
        FakeBrowserStore fake = new() { QuotaFull = true };
        OutboxStore outbox = new(fake);
        await outbox.AddAsync(StorageKeys.AnonymousOutbox, Answer(1));
        fake.QuotaFull = false;

        MoveOutcome moved = await outbox.MoveAsync(StorageKeys.AnonymousOutbox, Key);

        Assert.Equal(new MoveOutcome(1, 0), moved);
        Assert.Equal([Id(1)], StoredIds(fake, Key));
        Assert.Empty(await outbox.ReadAsync(StorageKeys.AnonymousOutbox));
        Assert.Equal(StorageStatus.Persistent, outbox.Status);
    }

    [Fact]
    public async Task Load_CorruptedJson_KeepsRawAndStartsEmpty()
    {
        FakeBrowserStore fake = new();
        fake.SetRaw(Key, "{\"version\":1,\"answers\":[{\"clientAnswerId\":");
        OutboxStore outbox = new(fake);

        IReadOnlyList<AnswerDto> read = await outbox.ReadAsync(Key);

        Assert.Empty(read);
        Assert.Null(fake.Raw(Key));
        string quarantined = Assert.Single(fake.Keys, k => k.StartsWith(StorageKeys.CorruptPrefix, StringComparison.Ordinal));
        Assert.Equal("{\"version\":1,\"answers\":[{\"clientAnswerId\":", fake.Raw(quarantined));
        Assert.Equal(StorageStatus.Persistent, outbox.Status);

        await outbox.AddAsync(Key, Answer(1));
        Assert.Equal([Id(1)], StoredIds(fake, Key));
    }

    [Fact]
    public async Task Load_CorruptedJson_CopyFails_LeavesRawInPlaceAndRunsMemoryOnly()
    {
        FakeBrowserStore fake = new() { QuotaFull = true };
        fake.SetRaw(Key, "not json");
        OutboxStore outbox = new(fake);

        Assert.Empty(await outbox.ReadAsync(Key));
        await outbox.AddAsync(Key, Answer(1));

        Assert.Equal("not json", fake.Raw(Key));
        Assert.DoesNotContain(fake.Keys, k => k.StartsWith(StorageKeys.CorruptPrefix, StringComparison.Ordinal));
        Assert.Equal(StorageStatus.MemoryOnly, outbox.Status);
        Assert.Equal([Answer(1)], await outbox.ReadAsync(Key));
    }

    [Fact]
    public async Task Load_UnknownVersion_LeftUntouchedAndReportsNewerVersion()
    {
        const string newer = "{\"version\":2,\"answers\":[],\"shards\":[\"a\"]}";
        FakeBrowserStore fake = new();
        fake.SetRaw(Key, newer);
        OutboxStore outbox = new(fake);

        Assert.Empty(await outbox.ReadAsync(Key));
        Assert.Equal(StorageStatus.NewerVersion, outbox.Status);

        await outbox.AddAsync(Key, Answer(1));
        await outbox.RemoveAsync(Key, [Id(1)]);
        await outbox.AddAsync(Key, Answer(2));

        Assert.Equal(newer, fake.Raw(Key));
        Assert.Equal([Answer(2)], await outbox.ReadAsync(Key));
        Assert.Equal(StorageStatus.NewerVersion, outbox.Status);
    }

    [Fact]
    public async Task Load_InvalidEntry_ExcludedButKept()
    {
        FakeBrowserStore fake = new();
        string badId = Entry(2).Replace(Id(2), "not-a-ulid", StringComparison.Ordinal);
        fake.SetRaw(Key, Document(Entry(1), badId, Entry(3, grade: "great"), Entry(4, answeredAt: "null"), "{\"clientAnswerId\":5}"));
        OutboxStore outbox = new(fake);

        IReadOnlyList<AnswerDto> read = await outbox.ReadAsync(Key);
        await outbox.AddAsync(Key, Answer(6));

        Assert.Equal([Id(1)], read.Select(a => a.ClientAnswerId));
        using JsonDocument doc = JsonDocument.Parse(fake.Raw(Key)!);
        Assert.Equal(6, doc.RootElement.GetProperty("answers").GetArrayLength());
        Assert.Equal([Id(1), Id(6)], (await outbox.ReadAsync(Key)).Select(a => a.ClientAnswerId));
    }

    [Fact]
    public async Task Add_QuotaExceeded_KeepsSnapshotInMemoryAndReports()
    {
        FakeBrowserStore fake = new();
        fake.SetRaw(Key, Document(Entry(1)));
        OutboxStore outbox = new(fake);
        int changes = 0;
        outbox.Changed += () => changes++;
        await outbox.ReadAsync(Key);
        string before = fake.Raw(Key)!;
        fake.QuotaFull = true;

        await outbox.AddAsync(Key, Answer(2));

        Assert.Equal(StorageStatus.Full, outbox.Status);
        Assert.Equal(1, outbox.UnsavedCount);
        Assert.True(changes > 0);
        Assert.Equal(before, fake.Raw(Key));
        Assert.Equal([Id(1), Id(2)], (await outbox.ReadAsync(Key)).Select(a => a.ClientAnswerId));
    }

    [Fact]
    public async Task Add_AfterQuotaFreed_PersistsWholeSnapshot()
    {
        FakeBrowserStore fake = new();
        fake.SetRaw(Key, Document(Entry(1)));
        OutboxStore outbox = new(fake);
        await outbox.ReadAsync(Key);
        fake.QuotaFull = true;
        await outbox.AddAsync(Key, Answer(2));
        await outbox.AddAsync(Key, Answer(3));
        Assert.Equal(2, outbox.UnsavedCount);

        fake.QuotaFull = false;
        await outbox.AddAsync(Key, Answer(4));

        Assert.Equal([Id(1), Id(2), Id(3), Id(4)], StoredIds(fake, Key));
        Assert.Equal(0, outbox.UnsavedCount);
        Assert.Equal(StorageStatus.Persistent, outbox.Status);
    }

    [Fact]
    public async Task Outbox_NeverEvictsPendingAnswers()
    {
        const int count = 10_000;
        FakeBrowserStore fake = new();
        OutboxStore outbox = new(fake);

        for (int n = 1; n <= count; n++)
        {
            await outbox.AddAsync(Key, Answer(n));
        }

        Assert.Equal(count, StoredIds(fake, Key).Length);
        IReadOnlyList<AnswerDto> read = await outbox.ReadAsync(Key);
        Assert.Equal(count, read.Count);
        Assert.Equal(Id(1), read[0].ClientAnswerId);
    }

    [Fact]
    public async Task Outbox_StorageFull_NeverEvictsPendingAnswers()
    {
        const int count = 300;
        FakeBrowserStore fake = new() { QuotaFull = true };
        OutboxStore outbox = new(fake);

        for (int n = 1; n <= count; n++)
        {
            await outbox.AddAsync(Key, Answer(n));
        }

        Assert.Equal(count, outbox.UnsavedCount);
        Assert.Equal(count, (await outbox.ReadAsync(Key)).Count);
    }

    [Fact]
    public async Task NoLocks_SwitchesToMemoryOnly()
    {
        FakeBrowserStore fake = new() { LocksAvailable = false };
        fake.SetRaw(Key, Document(Entry(1)));
        string before = fake.Raw(Key)!;
        OutboxStore outbox = new(fake);

        await outbox.AddAsync(Key, Answer(2));
        await outbox.AddAsync(Key, Answer(3));
        await outbox.RemoveAsync(Key, [Id(1)]);

        Assert.Equal(StorageStatus.MemoryOnly, outbox.Status);
        Assert.Equal(0, fake.Writes);
        Assert.Equal(before, fake.Raw(Key));
        Assert.Equal([Id(2), Id(3)], (await outbox.ReadAsync(Key)).Select(a => a.ClientAnswerId));
    }

    [Fact]
    public async Task Add_ConcurrentAppends_SecondFailsOnQuota_KeepsSecondUnsaved()
    {
        FakeBrowserStore fake = new();
        InterceptingStore store = new(fake);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Next.Enqueue(async op =>
        {
            await release.Task;
            return await fake.MutateAsync(op);
        });
        store.Next.Enqueue(_ => Task.FromResult(StoreResult.Failed(StoreErrors.Quota)));
        OutboxStore outbox = new(store);

        Task first = outbox.AddAsync(Key, Answer(1)).AsTask();
        await outbox.AddAsync(Key, Answer(2));
        release.SetResult();
        await first;

        Assert.Equal([Id(1)], StoredIds(fake, Key));
        Assert.Equal(1, outbox.UnsavedCount);
        Assert.Equal(StorageStatus.Full, outbox.Status);
        Assert.Equal([Id(1), Id(2)], (await outbox.ReadAsync(Key)).Select(a => a.ClientAnswerId));
    }

    [Fact]
    public async Task Remove_ConcurrentRemoves_SecondFails_KeepsSecondHidden()
    {
        FakeBrowserStore fake = new();
        fake.SetRaw(Key, Document(Entry(1), Entry(2), Entry(3)));
        InterceptingStore store = new(fake);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Next.Enqueue(async op =>
        {
            await release.Task;
            return await fake.MutateAsync(op);
        });
        store.Next.Enqueue(_ => Task.FromResult(StoreResult.Failed(StoreErrors.WriteFailed)));
        OutboxStore outbox = new(store);

        Task first = outbox.RemoveAsync(Key, [Id(1)]).AsTask();
        await outbox.RemoveAsync(Key, [Id(2)]);
        release.SetResult();
        await first;

        Assert.Equal([Id(2), Id(3)], StoredIds(fake, Key));
        Assert.Equal([Id(3)], (await outbox.ReadAsync(Key)).Select(a => a.ClientAnswerId));
        await outbox.RemoveAsync(Key, [Id(3)]);
        Assert.Empty(StoredIds(fake, Key));
    }

    [Fact]
    public async Task Move_SourceLooksEmptyWhenRead_StillMovesWhatTheScriptFinds()
    {
        FakeBrowserStore fake = new();
        fake.SetRaw(StorageKeys.AnonymousOutbox, Document(Entry(1)));
        InterceptingStore store = new(fake) { HiddenKey = StorageKeys.AnonymousOutbox };
        OutboxStore outbox = new(store);

        MoveOutcome moved = await outbox.MoveAsync(StorageKeys.AnonymousOutbox, Key);

        Assert.Equal(new MoveOutcome(1, 0), moved);
        Assert.Equal([Id(1)], StoredIds(fake, Key));
        Assert.Null(fake.Raw(StorageKeys.AnonymousOutbox));
    }

    [Fact]
    public async Task Move_SourceRemovalFails_IsNotCountedAsMoved_AndTheCleanupIsRetried()
    {
        FakeBrowserStore fake = new();
        fake.SetRaw(StorageKeys.AnonymousOutbox, Document(Entry(1), Entry(2)));
        fake.FailingRemovals.Add(StorageKeys.AnonymousOutbox);
        OutboxStore outbox = new(fake);

        MoveOutcome first = await outbox.MoveAsync(StorageKeys.AnonymousOutbox, Key);

        // The destination holds them, the source still does: nothing counts as moved yet.
        Assert.Equal(new MoveOutcome(0, 2), first);
        Assert.Equal([Id(1), Id(2)], StoredIds(fake, Key));
        Assert.Equal([Id(1), Id(2)], StoredIds(fake, StorageKeys.AnonymousOutbox));

        fake.FailingRemovals.Clear();
        MoveOutcome second = await outbox.MoveAsync(StorageKeys.AnonymousOutbox, Key);

        Assert.Equal(new MoveOutcome(2, 0), second);
        Assert.Null(fake.Raw(StorageKeys.AnonymousOutbox));
        Assert.Equal([Id(1), Id(2)], StoredIds(fake, Key));
    }

    [Fact]
    public async Task Move_AfterAFailedRemoval_IntoAnotherOwner_NeverDuplicatesAnAnswer()
    {
        const string OtherKey = "dit.outbox.v1.u.43";
        FakeBrowserStore fake = new();
        fake.SetRaw(StorageKeys.AnonymousOutbox, Document(Entry(1), Entry(2)));
        fake.FailingRemovals.Add(StorageKeys.AnonymousOutbox);
        await new OutboxStore(fake).MoveAsync(StorageKeys.AnonymousOutbox, Key);
        fake.FailingRemovals.Clear();
        fake.SetRaw(StorageKeys.AnonymousOutbox, Document(Entry(1), Entry(2), Entry(3)));

        // Another page, signed in as someone else: the answers already in user 42's outbox are his and only leave the source.
        MoveOutcome moved = await new OutboxStore(fake).MoveAsync(StorageKeys.AnonymousOutbox, OtherKey);

        Assert.Equal(new MoveOutcome(1, 0), moved);
        Assert.Equal([Id(3)], StoredIds(fake, OtherKey));
        Assert.Equal([Id(1), Id(2)], StoredIds(fake, Key));
        Assert.Null(fake.Raw(StorageKeys.AnonymousOutbox));
    }

    [Fact]
    public async Task Remove_WriteFailed_IsNotPersistentAndIsRetriedByTheNextAdd()
    {
        FakeBrowserStore fake = new();
        OutboxStore outbox = new(fake);
        await outbox.AddAsync(Key, Answer(1));
        await outbox.AddAsync(Key, Answer(2));
        fake.FailingRemovals.Add(Key);

        await outbox.RemoveAsync(Key, [Id(1)]);

        Assert.Equal([Id(1), Id(2)], StoredIds(fake, Key));
        Assert.NotEqual(StorageStatus.Persistent, outbox.Status);
        Assert.Equal(StorageStatus.WriteFailed, outbox.Status);

        fake.FailingRemovals.Clear();
        await outbox.AddAsync(Key, Answer(3));

        Assert.Equal([Id(2), Id(3)], StoredIds(fake, Key));
        Assert.Equal(StorageStatus.Persistent, outbox.Status);
    }

    [Fact]
    public async Task Remove_KeepsEntriesWithoutAnId()
    {
        FakeBrowserStore fake = new();
        fake.SetRaw(Key, Document("null", Entry(1)));
        OutboxStore outbox = new(fake);

        await outbox.RemoveAsync(Key, [Id(1)]);

        // Acknowledging answers never touches entries no id can name.
        Assert.DoesNotContain(Id(1), fake.Raw(Key), StringComparison.Ordinal);
        Assert.Contains("null", fake.Raw(Key), StringComparison.Ordinal);
    }

    /// <summary>Passes ops to the fake unless a scripted handler is queued; can hide one key from reads.</summary>
    private sealed class InterceptingStore(FakeBrowserStore inner) : IBrowserStore
    {
        public Queue<Func<StoreOp, Task<StoreResult>>> Next { get; } = new();

        public string? HiddenKey { get; init; }

        public async ValueTask<StoreResult> MutateAsync(StoreOp op) =>
            Next.TryDequeue(out Func<StoreOp, Task<StoreResult>>? handler) ? await handler(op) : await inner.MutateAsync(op);

        public ValueTask<string?> ReadAsync(string key) =>
            string.Equals(key, HiddenKey, StringComparison.Ordinal) ? ValueTask.FromResult<string?>(null) : inner.ReadAsync(key);
    }
}
