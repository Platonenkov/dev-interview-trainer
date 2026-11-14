using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Trainer.Contracts;
using Trainer.Web.Client.Api;
using Trainer.Web.Client.Storage;
using Trainer.Web.Client.Sync;

namespace Trainer.Web.Client.Tests;

/// <summary>The profile's anonymous import (spec 4, 11.4): only an emptied anonymous key counts as imported.</summary>
public sealed class AnonymousImportTests
{
    private const long Owner = 42;
    private const string QuestionId = "01KBZ3Q8N9V2K3M4P5R6S7T8WA";
    private static readonly DateTimeOffset DeviceStart = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Id(int n) => "01KC" + n.ToString("D22", CultureInfo.InvariantCulture);

    private static string Document(params int[] ids) =>
        "{\"version\":1,\"answers\":["
        + string.Join(',', ids.Select(n => $"{{\"clientAnswerId\":\"{Id(n)}\",\"questionId\":\"{QuestionId}\",\"grade\":\"knew\",\"usedHint\":false,\"mode\":\"practice\",\"answeredAt\":\"2026-11-12T09:00:00+00:00\"}}"))
        + "]}";

    private sealed class Harness
    {
        public Harness(params int[] anonymous)
            : this(null, anonymous)
        {
        }

        /// <param name="wrap">Puts a hook between the code under test and <see cref="Store"/>.</param>
        public Harness(Func<FakeBrowserStore, IBrowserStore>? wrap, params int[] anonymous)
        {
            FakeTimeProvider time = new(DeviceStart);
            IBrowserStore store = wrap is null ? Store : wrap(Store);
            Store.SetRaw(StorageKeys.AnonymousOutbox, Document(anonymous));
            Handler.Respond = r => string.Equals(r.Path, "/api/answers/batch", StringComparison.Ordinal) ? FakeHttpHandler.AcknowledgeAll(r) : null;
            OutboxStore outbox = new(store);
            HttpClient http = Handler.Client();
            Sync = new OutboxSyncService(
                outbox,
                new SummaryStore(store),
                new TrainerApiClient(http, new AntiforgeryTokenProvider(http), new ServerClock(time, store), time),
                time,
                Watcher);
            Import = new AnonymousImport(outbox, store, Sync, Watcher);
        }

        public FakeBrowserStore Store { get; } = new();

        public FakeHttpHandler Handler { get; } = new();

        public AccountWatcher Watcher { get; } = new(new FakeJsRuntime());

        public OutboxSyncService Sync { get; }

        public AnonymousImport Import { get; }

        public async Task<ImportResult> ImportAsync()
        {
            await Watcher.StartAsync(Owner.ToString(CultureInfo.InvariantCulture));
            Sync.Start(Owner);
            return await Import.ImportAsync(Owner, TimeSpan.FromSeconds(5), Ct);
        }
    }

    [Fact]
    public async Task Import_MovesAndSends_ReportsImported()
    {
        Harness h = new(1, 2);

        ImportResult result = await h.ImportAsync();

        Assert.Equal(new ImportResult(ImportStatus.Imported, 2, 0), result);
        Assert.Null(h.Store.Raw(StorageKeys.AnonymousOutbox));
        Assert.Empty(await h.Import.PendingIdsAsync());
    }

    [Fact]
    public async Task Import_AnswersClaimedByAnotherTabMeanwhile_AreStillCountedAsImported()
    {
        HookStore? hook = null;
        Harness h = new(
            inner =>
            {
                hook = new HookStore(inner);
                return hook;
            },
            1,
            2);
        // The other tab moves them into the owner's outbox between this page's reading the key and its own move, which
        // then moves nothing.
        hook!.Intercept = op =>
        {
            if (string.Equals(op.Op, "move", StringComparison.Ordinal))
            {
                h.Store.SetRaw(StorageKeys.AnonymousOutbox, Document());
                h.Store.SetRaw(StorageKeys.UserOutbox(Owner), Document(1, 2));
            }

            return ValueTask.FromResult<StoreResult?>(null);
        };

        ImportResult result = await h.ImportAsync();

        Assert.Equal(ImportStatus.Imported, result.Status);
        Assert.Equal(2, result.Moved);
    }

    [Fact]
    public async Task Import_AnswersDiscardedByAnotherTabMeanwhile_AreNotCountedAsImported()
    {
        HookStore? hook = null;
        Harness h = new(
            inner =>
            {
                hook = new HookStore(inner);
                return hook;
            },
            1,
            2);
        hook!.Intercept = op =>
        {
            if (string.Equals(op.Op, "move", StringComparison.Ordinal))
            {
                h.Store.SetRaw(StorageKeys.AnonymousOutbox, Document());
            }

            return ValueTask.FromResult<StoreResult?>(null);
        };

        ImportResult result = await h.ImportAsync();

        Assert.Equal(0, result.Moved);
        Assert.Null(h.Store.Raw(StorageKeys.UserOutbox(Owner)));
    }

    [Fact]
    public async Task Import_Incomplete_AsksTheSyncToSendTheCopies()
    {
        Harness h = new(1, 2);
        h.Store.FailingRemovals.Add(StorageKeys.AnonymousOutbox);

        ImportResult result = await h.ImportAsync();

        Assert.Equal(ImportStatus.Incomplete, result.Status);
        // The copies in the owner's outbox go out without waiting for the next page load.
        for (int i = 0; i < 200 && !h.Handler.Requests.Any(r => string.Equals(r.Path, "/api/answers/batch", StringComparison.Ordinal)); i++)
        {
            await Task.Delay(10, Ct);
        }

        Assert.Contains(h.Handler.Requests, r => string.Equals(r.Path, "/api/answers/batch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Import_SourceRemovalFails_IsNotReportedAsImported()
    {
        Harness h = new(1, 2);
        h.Store.FailingRemovals.Add(StorageKeys.AnonymousOutbox);

        ImportResult result = await h.ImportAsync();

        Assert.Equal(ImportStatus.Incomplete, result.Status);
        Assert.Equal(2, result.StillHere);
        Assert.Equal([Id(1), Id(2)], await h.Import.PendingIdsAsync());

        // Trying again once storage works finishes the move without a second copy.
        h.Store.FailingRemovals.Clear();
        ImportResult again = await h.Import.ImportAsync(Owner, TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(ImportStatus.Imported, again.Status);
        Assert.Empty(await h.Import.PendingIdsAsync());
    }

    [Fact]
    public async Task Import_ReadAfterTheMoveFails_IsReportedIncomplete()
    {
        UnreadableAfterMoveStore? unreadable = null;
        Harness h = new(
            inner =>
            {
                unreadable = new UnreadableAfterMoveStore(inner);
                return unreadable;
            },
            1,
            2);
        // The move writes the destination but cannot rewrite the source: the answers are still under the anonymous key.
        h.Store.FailingRemovals.Add(StorageKeys.AnonymousOutbox);

        ImportResult result = await h.ImportAsync();

        // The direct read that would show them fails; that must not be read as "the key is empty".
        Assert.True(unreadable!.FailedReads > 0);
        Assert.Equal(ImportStatus.Incomplete, result.Status);
        Assert.Null(result.StillHere);
        Assert.NotNull(h.Store.Raw(StorageKeys.AnonymousOutbox));
    }

    [Fact]
    public async Task Import_MoveSucceedsButTheReadAfterItFails_ReportsNoCount()
    {
        UnreadableAfterMoveStore? unreadable = null;
        Harness h = new(
            inner =>
            {
                unreadable = new UnreadableAfterMoveStore(inner);
                return unreadable;
            },
            1,
            2);

        ImportResult result = await h.ImportAsync();

        // Nothing is left, but this page cannot tell: no number ("0 answers are still here") is better than a wrong one.
        Assert.Null(h.Store.Raw(StorageKeys.AnonymousOutbox));
        Assert.Equal(ImportStatus.Incomplete, result.Status);
        Assert.Null(result.StillHere);
    }

    [Fact]
    public async Task Import_NewerVersionUnderTheAnonymousKey_IsNotReportedAsImported()
    {
        Harness h = new(1);
        const string newer = "{\"version\":2,\"answers\":[]}";
        h.Store.SetRaw(StorageKeys.AnonymousOutbox, newer);

        ImportResult result = await h.ImportAsync();

        Assert.Equal(ImportStatus.Incomplete, result.Status);
        Assert.Equal(newer, h.Store.Raw(StorageKeys.AnonymousOutbox));
    }

    [Fact]
    public async Task Discard_ReadAfterTheRemovalFails_IsReportedFailed()
    {
        UnreadableAfterMoveStore? unreadable = null;
        Harness h = new(
            inner =>
            {
                unreadable = new UnreadableAfterMoveStore(inner) { FailAfter = "remove" };
                return unreadable;
            },
            1);
        await h.Watcher.StartAsync(Owner.ToString(CultureInfo.InvariantCulture));
        h.Store.FailingRemovals.Add(StorageKeys.AnonymousOutbox);

        DiscardResult result = await h.Import.DiscardAsync([Id(1)]);

        Assert.Equal(DiscardResult.Failed, result);
    }

    /// <summary>Checked reads of the anonymous key fail once an op of the given kind ran, as a throwing <c>localStorage</c> would.</summary>
    private sealed class UnreadableAfterMoveStore(FakeBrowserStore inner) : IBrowserStore
    {
        private bool _failing;

        public string FailAfter { get; init; } = "move";

        public int FailedReads { get; private set; }

        public ValueTask<string?> ReadAsync(string key) => IsFailing(key) ? ValueTask.FromResult<string?>(null) : inner.ReadAsync(key);

        public async ValueTask<StoredRead> ReadCheckedAsync(string key)
        {
            if (IsFailing(key))
            {
                FailedReads++;
                return new StoredRead(false, null);
            }

            return new StoredRead(true, await inner.ReadAsync(key));
        }

        public async ValueTask<StoreResult> MutateAsync(StoreOp op)
        {
            StoreResult result = await inner.MutateAsync(op);
            _failing |= string.Equals(op.Op, FailAfter, StringComparison.Ordinal);
            return result;
        }

        private bool IsFailing(string key) => _failing && string.Equals(key, StorageKeys.AnonymousOutbox, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_AfterAnotherTabChangedTheAccount_IsRefused()
    {
        Harness h = new(1);
        await h.Watcher.StartAsync(Owner.ToString(CultureInfo.InvariantCulture));
        h.Sync.Start(Owner);
        h.Watcher.OnAccountPublished("7");

        Assert.False(h.Import.CanAct);
        ImportResult result = await h.Import.ImportAsync(Owner, TimeSpan.FromSeconds(5), Ct);
        DiscardResult discard = await h.Import.DiscardAsync([Id(1)]);

        Assert.Equal(ImportStatus.Refused, result.Status);
        Assert.Equal(DiscardResult.Refused, discard);
        Assert.Equal([Id(1)], await h.Import.PendingIdsAsync());
        Assert.Null(h.Store.Raw(StorageKeys.UserOutbox(Owner)));
    }

    [Fact]
    public async Task Discard_RemovesOnlyTheCountedAnswers()
    {
        Harness h = new(1, 2);
        IReadOnlyList<string> counted = await h.Import.PendingIdsAsync();
        h.Store.SetRaw(StorageKeys.AnonymousOutbox, Document(1, 2, 3));

        DiscardResult result = await h.Import.DiscardAsync(counted);

        Assert.Equal(DiscardResult.Discarded, result);
        Assert.Equal([Id(3)], await h.Import.PendingIdsAsync());
    }

    [Fact]
    public async Task Discard_WriteFails_IsReported()
    {
        Harness h = new(1, 2);
        h.Store.FailingRemovals.Add(StorageKeys.AnonymousOutbox);

        DiscardResult result = await h.Import.DiscardAsync(await h.Import.PendingIdsAsync());

        Assert.Equal(DiscardResult.Failed, result);
        Assert.Equal([Id(1), Id(2)], await h.Import.PendingIdsAsync());
    }
}
