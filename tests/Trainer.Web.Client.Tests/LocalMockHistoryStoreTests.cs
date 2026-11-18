using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Trainer.Contracts;
using Trainer.Contracts.Storage;
using Trainer.Web.Client.Storage;

namespace Trainer.Web.Client.Tests;

public sealed class LocalMockHistoryStoreTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private const string Stack = "dotnet";

    private static string Ulid(int n) => "01KD" + n.ToString("D22", CultureInfo.InvariantCulture);

    private static MockHistoryDto Mock(int n, int knew = 3) =>
        new(Ulid(n), "middle", 5, At.AddMinutes(n), knew, 1, 1, 0);

    private static JsonArray StoredItems(FakeBrowserStore fake) =>
        (JsonArray)JsonNode.Parse(fake.Raw(StorageKeys.AnonymousMockHistory)!)!["items"]!;

    [Fact]
    public async Task Add_KeepsTheNewest20AndMergesById()
    {
        FakeBrowserStore fake = new();
        LocalMockHistoryStore history = new(fake);

        // Added out of order on purpose: the order of arrival must not decide what is kept.
        foreach (int n in Enumerable.Range(1, 21).Reverse())
        {
            Assert.True((await history.AddAsync(Stack, Mock(n))).Ok);
        }

        IReadOnlyList<MockHistoryDto> items = await history.ReadAsync(Stack);
        Assert.Equal(StoredMockHistory.MaxItems, items.Count);
        Assert.Equal(Enumerable.Range(2, 20).Reverse().Select(Ulid), items.Select(i => i.Id));
        Assert.Equal(20, StoredItems(fake).Count);

        Assert.True((await history.AddAsync(Stack, Mock(10, knew: 5))).Ok);

        IReadOnlyList<MockHistoryDto> replaced = await history.ReadAsync(Stack);
        Assert.Equal(20, replaced.Count);
        Assert.Equal(5, replaced.Single(i => i.Id == Ulid(10)).Knew);
        Assert.Equal(20, StoredItems(fake).Count);
        Assert.Equal(StorageStatus.Persistent, history.Status);
    }

    [Fact]
    public async Task Add_AnOlderMockThanTheKeptTwenty_IsNotKept()
    {
        FakeBrowserStore fake = new();
        LocalMockHistoryStore history = new(fake);
        foreach (int n in Enumerable.Range(10, 20))
        {
            await history.AddAsync(Stack, Mock(n));
        }

        StoreResult result = await history.AddAsync(Stack, Mock(1));

        Assert.True(result.Ok);
        Assert.DoesNotContain(await history.ReadAsync(Stack), i => i.Id == Ulid(1));
        Assert.Equal(20, StoredItems(fake).Count);
    }

    [Fact]
    public async Task Add_FromTwoTabs_KeepsBoth()
    {
        FakeBrowserStore fake = new();
        HookStore first = new(fake);
        HookStore second = new(fake);
        LocalMockHistoryStore tabA = new(first);
        LocalMockHistoryStore tabB = new(second);
        TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Intercept = async _ =>
        {
            held.SetResult();
            await released.Task;
            return null;
        };

        Task<StoreResult> addA = tabA.AddAsync(Stack, Mock(1)).AsTask();
        await held.Task;
        // Tab B writes while tab A's op is still waiting for its turn: a whole-document write would lose one of them.
        Assert.True((await tabB.AddAsync(Stack, Mock(2))).Ok);
        released.SetResult();
        Assert.True((await addA).Ok);

        Assert.Equal([Ulid(2), Ulid(1)], (await tabA.ReadAsync(Stack)).Select(i => i.Id));
        Assert.Equal([Ulid(2), Ulid(1)], (await tabB.ReadAsync(Stack)).Select(i => i.Id));
        Assert.All(fake.Ops, op => Assert.Equal("historyAdd", op.Op));
    }

    [Fact]
    public async Task Add_WithoutLocks_KeepsTheItemInMemoryAndReportsMemoryOnly()
    {
        FakeBrowserStore fake = new() { LocksAvailable = false };
        LocalMockHistoryStore history = new(fake);
        int changes = 0;
        history.Changed += () => changes++;

        StoreResult result = await history.AddAsync(Stack, Mock(1));

        Assert.False(result.Persisted);
        Assert.Equal(StorageStatus.MemoryOnly, history.Status);
        Assert.Equal([Ulid(1)], (await history.ReadAsync(Stack)).Select(i => i.Id));
        Assert.Null(fake.Raw(StorageKeys.AnonymousMockHistory));
        Assert.True(changes > 0);

        // The tab stopped writing: a later add is not even tried.
        int ops = fake.Ops.Count;
        await history.AddAsync(Stack, Mock(2));
        Assert.Equal(ops, fake.Ops.Count);
        Assert.Equal([Ulid(2), Ulid(1)], (await history.ReadAsync(Stack)).Select(i => i.Id));
    }

    [Fact]
    public async Task Add_WhenStorageIsFull_HoldsTheItemAndTheNextAddRetriesIt()
    {
        FakeBrowserStore fake = new() { QuotaFull = true };
        LocalMockHistoryStore history = new(fake);

        StoreResult full = await history.AddAsync(Stack, Mock(1));

        Assert.False(full.Persisted);
        Assert.Equal(StorageStatus.Full, history.Status);
        Assert.Equal([Ulid(1)], (await history.ReadAsync(Stack)).Select(i => i.Id));
        Assert.Null(fake.Raw(StorageKeys.AnonymousMockHistory));

        fake.QuotaFull = false;
        Assert.True((await history.AddAsync(Stack, Mock(2))).Ok);

        Assert.Equal(StorageStatus.Persistent, history.Status);
        Assert.Equal([Ulid(2), Ulid(1)], StoredItems(fake).Select(i => i!["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Add_RefusedForQuota_MakesRoomAndIsTriedOnceMore()
    {
        FakeBrowserStore fake = new() { QuotaFull = true };
        List<string> asked = [];
        LocalMockHistoryStore history = new(fake)
        {
            MakeRoomAsync = key =>
            {
                asked.Add(key);
                fake.QuotaFull = false;
                return ValueTask.FromResult(true);
            },
        };

        StoreResult result = await history.AddAsync(Stack, Mock(1));

        Assert.True(result.Persisted);
        Assert.Equal([StorageKeys.AnonymousMockHistory], asked);
        Assert.Equal(StorageStatus.Persistent, history.Status);
        Assert.Equal([Ulid(1)], StoredItems(fake).Select(i => i!["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Add_RefusedForQuota_WhenNoRoomWasMade_IsNotRetriedAndIsHeld()
    {
        FakeBrowserStore fake = new() { QuotaFull = true };
        int asked = 0;
        LocalMockHistoryStore history = new(fake)
        {
            MakeRoomAsync = _ =>
            {
                asked++;
                return ValueTask.FromResult(false);
            },
        };
        int before = fake.Ops.Count;

        StoreResult result = await history.AddAsync(Stack, Mock(1));

        Assert.False(result.Persisted);
        Assert.Equal(1, asked);
        Assert.Equal(1, fake.Ops.Count - before);
        Assert.Equal(StorageStatus.Full, history.Status);
    }

    [Fact]
    public async Task Read_ACorruptDocument_IsQuarantinedAndTheHistoryStartsEmpty()
    {
        FakeBrowserStore fake = new();
        fake.SetRaw(StorageKeys.AnonymousMockHistory, "{not json");
        LocalMockHistoryStore history = new(fake);

        Assert.Empty(await history.ReadAsync(Stack));

        Assert.Null(fake.Raw(StorageKeys.AnonymousMockHistory));
        Assert.Contains(fake.Keys, k => k.StartsWith(StorageKeys.CorruptPrefix, StringComparison.Ordinal));
        Assert.True((await history.AddAsync(Stack, Mock(1))).Ok);
        Assert.Equal([Ulid(1)], (await history.ReadAsync(Stack)).Select(i => i.Id));
    }

    [Fact]
    public async Task Add_ToACorruptDocument_QuarantinesItAndWritesTheItem()
    {
        FakeBrowserStore fake = new();
        fake.SetRaw(StorageKeys.AnonymousMockHistory, "[1,2");
        LocalMockHistoryStore history = new(fake);

        Assert.True((await history.AddAsync(Stack, Mock(1))).Ok);

        Assert.Equal([Ulid(1)], StoredItems(fake).Select(i => i!["id"]!.GetValue<string>()));
        Assert.Contains(fake.Keys, k => k.StartsWith(StorageKeys.CorruptPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Read_ACorruptDocumentThatCannotBeSetAside_IsLeftInPlaceAndRunsMemoryOnly()
    {
        FakeBrowserStore fake = new() { LocksAvailable = false };
        fake.SetRaw(StorageKeys.AnonymousMockHistory, "garbage");
        LocalMockHistoryStore history = new(fake);

        Assert.Empty(await history.ReadAsync(Stack));
        Assert.Equal(StorageStatus.MemoryOnly, history.Status);

        await history.AddAsync(Stack, Mock(1));

        Assert.Equal("garbage", fake.Raw(StorageKeys.AnonymousMockHistory));
        Assert.Equal([Ulid(1)], (await history.ReadAsync(Stack)).Select(i => i.Id));
    }

    [Fact]
    public async Task ANewerAppsDocument_IsNeverTouched()
    {
        const string Newer = "{\"version\":2,\"items\":[{\"id\":\"x\"}]}";
        FakeBrowserStore fake = new();
        fake.SetRaw(StorageKeys.AnonymousMockHistory, Newer);
        LocalMockHistoryStore history = new(fake);

        Assert.Empty(await history.ReadAsync(Stack));
        StoreResult result = await history.AddAsync(Stack, Mock(1));

        Assert.False(result.Persisted);
        Assert.Equal(StorageStatus.NewerVersion, history.Status);
        Assert.Equal(Newer, fake.Raw(StorageKeys.AnonymousMockHistory));
        Assert.Equal([Ulid(1)], (await history.ReadAsync(Stack)).Select(i => i.Id));
    }

    [Fact]
    public async Task Read_SkipsInvalidEntries_AndReturnsOnlyTheNewest20OfAnOversizedDocument()
    {
        FakeBrowserStore fake = new();
        JsonArray items = [.. Enumerable.Range(1, 25).Select(n => Stored(n, Stack))];
        items.Add(new JsonObject { ["id"] = "not-a-ulid", ["stack"] = Stack, ["level"] = "middle", ["finishedAt"] = At.AddDays(9) });
        items.Add(new JsonObject { ["id"] = Ulid(99), ["stack"] = Stack, ["level"] = "wizard", ["questionCount"] = 5, ["finishedAt"] = At.AddDays(9) });
        // Entries with no stack or a stack that cannot be one are never shown.
        JsonObject noStack = Stored(98, Stack);
        noStack.Remove("stack");
        items.Add(noStack);
        items.Add(Stored(97, "Not A Stack"));
        items.Add(JsonValue.Create(7));
        fake.SetRaw(StorageKeys.AnonymousMockHistory, new JsonObject { ["version"] = 1, ["items"] = items }.ToJsonString());
        LocalMockHistoryStore history = new(fake);

        IReadOnlyList<MockHistoryDto> read = await history.ReadAsync(Stack);

        Assert.Equal(Enumerable.Range(6, 20).Reverse().Select(Ulid), read.Select(i => i.Id));
    }

    [Fact]
    public async Task TwoStacks_KeepTheirOwnMocksAndTheirOwnTwentyEach()
    {
        FakeBrowserStore fake = new();
        LocalMockHistoryStore history = new(fake);
        foreach (int n in Enumerable.Range(1, 25))
        {
            await history.AddAsync("dotnet", Mock(n));
        }

        foreach (int n in Enumerable.Range(101, 3))
        {
            await history.AddAsync("java", Mock(n));
        }

        IReadOnlyList<MockHistoryDto> dotnet = await history.ReadAsync("dotnet");
        IReadOnlyList<MockHistoryDto> java = await history.ReadAsync("java");

        Assert.Equal(Enumerable.Range(6, 20).Reverse().Select(Ulid), dotnet.Select(i => i.Id));
        Assert.Equal([Ulid(103), Ulid(102), Ulid(101)], java.Select(i => i.Id));
        Assert.Empty(await history.ReadAsync("rust"));
        // 20 of one stack never push out the other stack's mocks.
        Assert.Equal(23, StoredItems(fake).Count);
    }

    [Fact]
    public async Task Add_ReadsBackThroughAnotherStore_ForTheStackItWasAddedUnder()
    {
        FakeBrowserStore fake = new();
        await new LocalMockHistoryStore(fake).AddAsync("java", Mock(1));

        LocalMockHistoryStore other = new(fake);

        Assert.Empty(await other.ReadAsync("dotnet"));
        Assert.Equal([Ulid(1)], (await other.ReadAsync("java")).Select(i => i.Id));
    }

    [Fact]
    public async Task FakeStore_HistoryAdd_RefusesAMissingTimeAndTakesAnUnreadableOneAsNow()
    {
        FakeBrowserStore fake = new();
        JsonElement Entry(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

        StoreResult missing = await fake.MutateAsync(StoreOp.HistoryAdd(StorageKeys.AnonymousMockHistory, Entry($"{{\"id\":\"{Ulid(1)}\",\"stack\":\"dotnet\"}}")));
        StoreResult noStack = await fake.MutateAsync(StoreOp.HistoryAdd(StorageKeys.AnonymousMockHistory, Entry($"{{\"id\":\"{Ulid(1)}\",\"finishedAt\":\"{At:O}\"}}")));
        StoreResult unreadable = await fake.MutateAsync(StoreOp.HistoryAdd(StorageKeys.AnonymousMockHistory, Entry($"{{\"id\":\"{Ulid(2)}\",\"stack\":\"dotnet\",\"finishedAt\":\"garbage\"}}")));

        Assert.Equal(StoreErrors.BadOp, missing.Error);
        Assert.Equal(StoreErrors.BadOp, noStack.Error);
        Assert.True(unreadable.Ok);
        Assert.Equal(1, unreadable.Count);
        Assert.True(DateTimeOffset.TryParse((string)StoredItems(fake).Single()!["finishedAt"]!, CultureInfo.InvariantCulture, out DateTimeOffset stored));
        Assert.True(stored > At);
    }

    private static JsonObject Stored(int n, string stack) =>
        (JsonObject)JsonSerializer.SerializeToNode(
            StoredMockHistoryItem.From(stack, Mock(n)),
            TrainerJsonContext.Default.StoredMockHistoryItem)!;

    [Fact]
    public void ToHistory_SumsTheTopicsAndKeepsTheSessionId()
    {
        MockSummaryDto summary = new(
            "dotnet",
            "senior",
            9,
            true,
            At,
            At.AddMinutes(30),
            [new MockTopicResultDto("a", 2, 1, 0, 1), new MockTopicResultDto("b", 1, 0, 3, 1)]);

        MockHistoryDto history = LocalMockHistoryStore.ToHistory(Ulid(5), summary);

        Assert.Equal(new MockHistoryDto(Ulid(5), "senior", 9, At.AddMinutes(30), 3, 1, 3, 2), history);
    }
}
