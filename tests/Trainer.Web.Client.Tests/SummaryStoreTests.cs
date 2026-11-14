using Trainer.Contracts;
using Trainer.Contracts.Storage;
using Trainer.Web.Client.Storage;

namespace Trainer.Web.Client.Tests;

public sealed class SummaryStoreTests
{
    private const long Owner = 4242;
    private const string First = "01KC0000000000000000000001";
    private const string Second = "01KC0000000000000000000002";
    private static readonly DateTimeOffset At = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private static MockSummaryDto Summary(int knew) =>
        new("dotnet", "middle", 5, true, At, At.AddMinutes(20), [new MockTopicResultDto("csharp", knew, 1, 1, 3 - knew)]);

    private static void AssertSummary(MockSummaryDto expected, MockSummaryDto actual)
    {
        Assert.Equal(expected with { Topics = [] }, actual with { Topics = [] });
        Assert.Equal(expected.Topics, actual.Topics);
    }

    [Fact]
    public async Task Summary_AddPending_ThenRead_MergesById()
    {
        FakeBrowserStore fake = new();
        SummaryStore summaries = new(fake);

        Assert.True((await summaries.AddPendingAsync(Owner, First, Summary(1))).Ok);
        Assert.True((await summaries.AddPendingAsync(Owner, Second, Summary(2))).Ok);
        Assert.True((await summaries.AddPendingAsync(Owner, First, Summary(3))).Ok);

        IReadOnlyList<StoredMockSummary> pending = await summaries.ReadPendingAsync(Owner);
        Assert.Equal([First, Second], pending.Select(s => s.MockSessionId));
        AssertSummary(Summary(3), pending[0].Summary);
        Assert.NotNull(fake.Raw(StorageKeys.PendingSummaries(Owner)));
        Assert.All(fake.Ops, op => Assert.True(op.Op is "summaryAdd"));
    }

    [Fact]
    public async Task Summary_RemovePending_OnlyRemovesThatSession()
    {
        FakeBrowserStore fake = new();
        SummaryStore summaries = new(fake);
        await summaries.AddPendingAsync(Owner, First, Summary(1));
        await summaries.AddPendingAsync(Owner, Second, Summary(2));

        Assert.True((await summaries.RemovePendingAsync(Owner, First)).Ok);

        Assert.Equal([Second], (await summaries.ReadPendingAsync(Owner)).Select(s => s.MockSessionId));
        Assert.Equal("summaryRemove", fake.Ops[^1].Op);
    }

    [Fact]
    public async Task Summary_MoveToFailed_IsRecoverable()
    {
        FakeBrowserStore fake = new();
        SummaryStore summaries = new(fake);
        await summaries.AddPendingAsync(Owner, First, Summary(2));
        await summaries.AddPendingAsync(Owner, Second, Summary(1));

        StoreResult moved = await summaries.MoveToFailedAsync(Owner, First, "validation");

        Assert.True(moved.Ok);
        Assert.Equal([Second], (await summaries.ReadPendingAsync(Owner)).Select(s => s.MockSessionId));
        StoredMockSummary failed = Assert.Single(await summaries.ReadFailedAsync(Owner));
        Assert.Equal(First, failed.MockSessionId);
        Assert.Equal("validation", failed.Reason);
        AssertSummary(Summary(2), failed.Summary);

        Assert.True((await summaries.AddPendingAsync(Owner, failed.MockSessionId, failed.Summary)).Ok);
        Assert.True((await summaries.DiscardFailedAsync(Owner, First)).Ok);
        Assert.Empty(await summaries.ReadFailedAsync(Owner));
        Assert.Equal([Second, First], (await summaries.ReadPendingAsync(Owner)).Select(s => s.MockSessionId));
    }

    [Fact]
    public async Task Summary_CorruptedQueue_IsQuarantinedAndReadsEmpty()
    {
        FakeBrowserStore fake = new();
        fake.SetRaw(StorageKeys.PendingSummaries(Owner), "{\"version\":1,\"items\":");
        SummaryStore summaries = new(fake);

        Assert.Empty(await summaries.ReadPendingAsync(Owner));
        Assert.Null(fake.Raw(StorageKeys.PendingSummaries(Owner)));
        Assert.Contains(fake.Keys, k => k.StartsWith(StorageKeys.CorruptPrefix, StringComparison.Ordinal));
        Assert.True((await summaries.AddPendingAsync(Owner, First, Summary(1))).Ok);
    }

    [Fact]
    public async Task Summary_NoLocks_ReportsFailureWithoutWriting()
    {
        FakeBrowserStore fake = new() { LocksAvailable = false };
        SummaryStore summaries = new(fake);

        StoreResult result = await summaries.AddPendingAsync(Owner, First, Summary(1));

        Assert.False(result.Ok);
        Assert.Equal(StoreErrors.NoLocks, result.Error);
        Assert.Equal(0, fake.Writes);
    }

    [Fact]
    public async Task Summary_NoLocks_KeepsPendingInMemoryUntilRemoved()
    {
        FakeBrowserStore fake = new() { LocksAvailable = false };
        SummaryStore summaries = new(fake);

        Assert.False((await summaries.AddPendingAsync(Owner, First, Summary(1))).Persisted);

        // The sync service reads the pending list; a summary that storage refused must still be in it.
        StoredMockSummary held = Assert.Single(await summaries.ReadPendingAsync(Owner));
        Assert.Equal(First, held.MockSessionId);
        Assert.Equal(1, summaries.UnsavedCount);
        Assert.Empty(await summaries.ReadPendingAsync(Owner + 1));

        // Removing a summary held only in memory succeeds, so a 204 does not leave the sync retrying forever.
        Assert.True((await summaries.RemovePendingAsync(Owner, First)).Ok);
        Assert.Empty(await summaries.ReadPendingAsync(Owner));
        Assert.Equal(0, summaries.UnsavedCount);
        Assert.Equal(0, fake.Writes);
    }

    [Fact]
    public async Task Summary_HoldPending_NeverWritesStorage()
    {
        FakeBrowserStore fake = new();
        SummaryStore summaries = new(fake);

        summaries.HoldPending(Owner, First, Summary(2));

        Assert.Equal([First], (await summaries.ReadPendingAsync(Owner)).Select(s => s.MockSessionId));
        Assert.DoesNotContain(fake.Ops, op => op.Op == "summaryAdd");
        Assert.Null(fake.Raw(StorageKeys.PendingSummaries(Owner)));

        // A later durable add of the same summary replaces the held copy.
        Assert.True((await summaries.AddPendingAsync(Owner, First, Summary(2))).Persisted);
        Assert.Equal(0, summaries.UnsavedCount);
        Assert.Single(await summaries.ReadPendingAsync(Owner));
    }

    [Fact]
    public async Task Summary_NoLocks_MoveToFailedAndDiscardWorkInMemory()
    {
        FakeBrowserStore fake = new() { LocksAvailable = false };
        SummaryStore summaries = new(fake);
        await summaries.AddPendingAsync(Owner, First, Summary(1));

        Assert.True((await summaries.MoveToFailedAsync(Owner, First, ApiErrorCodes.Validation)).Ok);

        Assert.Empty(await summaries.ReadPendingAsync(Owner));
        StoredMockSummary failed = Assert.Single(await summaries.ReadFailedAsync(Owner));
        Assert.Equal(First, failed.MockSessionId);
        Assert.Equal(ApiErrorCodes.Validation, failed.Reason);

        Assert.True((await summaries.DiscardFailedAsync(Owner, First)).Ok);
        Assert.Empty(await summaries.ReadFailedAsync(Owner));
        Assert.Equal(0, summaries.UnsavedCount);
    }

    [Fact]
    public async Task Summary_UnknownVersion_ReportsNewerVersionAndIsUntouched()
    {
        const string newer = "{\"version\":2,\"items\":[]}";
        FakeBrowserStore fake = new();
        fake.SetRaw(StorageKeys.PendingSummaries(Owner), newer);
        SummaryStore summaries = new(fake);
        int changes = 0;
        summaries.Changed += () => changes++;

        Assert.Empty(await summaries.ReadPendingAsync(Owner));
        Assert.True(summaries.HasNewerVersion);
        Assert.Equal(1, changes);

        StoreResult added = await summaries.AddPendingAsync(Owner, First, Summary(1));

        Assert.Equal(StoreErrors.NewerVersion, added.Error);
        Assert.Equal(newer, fake.Raw(StorageKeys.PendingSummaries(Owner)));
        Assert.True(summaries.HasNewerVersion);
        Assert.Empty(await summaries.ReadFailedAsync(Owner));
        Assert.True(summaries.HasNewerVersion);
    }
}
