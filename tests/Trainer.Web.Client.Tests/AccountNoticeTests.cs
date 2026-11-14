using System.Net;
using Microsoft.Extensions.Time.Testing;
using Trainer.Contracts;
using Trainer.Web.Client.Api;
using Trainer.Web.Client.Storage;
using Trainer.Web.Client.Sync;

namespace Trainer.Web.Client.Tests;

public sealed class AccountNoticeTests
{
    private const long Owner = 42;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (AccountNotice Notice, AccountWatcher Watcher, OutboxSyncService Sync, FakeHttpHandler Handler, OutboxStore Outbox) Create()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero));
        FakeBrowserStore store = new();
        OutboxStore outbox = new(store);
        FakeHttpHandler handler = new();
        HttpClient http = handler.Client();
        AccountWatcher watcher = new(new FakeJsRuntime());
        OutboxSyncService sync = new(
            outbox, new SummaryStore(store), new TrainerApiClient(http, new AntiforgeryTokenProvider(http), new ServerClock(time, store), time), time, watcher);
        return (new AccountNotice(watcher, sync), watcher, sync, handler, outbox);
    }

    [Fact]
    public async Task OtherTabsAccount_ShowsTheNoticeOnce()
    {
        (AccountNotice notice, AccountWatcher watcher, _, _, _) = Create();
        int changes = 0;
        notice.Changed += () => changes++;
        await watcher.StartAsync(Owner.ToString(System.Globalization.CultureInfo.InvariantCulture));

        watcher.OnAccountPublished("anon");
        notice.Show();

        Assert.True(notice.IsShown);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task ServerOwnerMismatch_ShowsTheNotice()
    {
        (AccountNotice notice, _, OutboxSyncService sync, FakeHttpHandler handler, OutboxStore outbox) = Create();
        await outbox.AddAsync(StorageKeys.UserOutbox(Owner), new AnswerDto(TestQuestions.NewId(), TestQuestions.NewId(), "knew", false, "practice", null, new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero)));
        handler.Respond = r => r.Path.EndsWith("/batch", StringComparison.Ordinal) ? FakeHttpHandler.Error(HttpStatusCode.Conflict, ApiErrorCodes.OwnerMismatch) : null;

        sync.Start(Owner);
        await sync.FlushAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.True(notice.IsShown);
    }

    [Fact]
    public void Show_ByAnIslandState_IsTheSameSingleNotice()
    {
        (AccountNotice notice, _, _, _, _) = Create();
        int changes = 0;
        notice.Changed += () => changes++;

        notice.Show();
        notice.Show();

        Assert.True(notice.IsShown);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task CreatedAfterTheChange_StartsShown()
    {
        FakeTimeProvider time = new();
        FakeBrowserStore store = new();
        HttpClient http = new FakeHttpHandler().Client();
        AccountWatcher watcher = new(new FakeJsRuntime());
        await watcher.StartAsync("anon");
        watcher.OnAccountPublished("7");
        OutboxSyncService sync = new(
            new OutboxStore(store), new SummaryStore(store), new TrainerApiClient(http, new AntiforgeryTokenProvider(http), new ServerClock(time, store), time), time, watcher);

        Assert.True(new AccountNotice(watcher, sync).IsShown);
    }

    private static AnswerDto AnyAnswer() =>
        new(TestQuestions.NewId(), TestQuestions.NewId(), "knew", false, "practice", null, new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task ServerSignedOut_ShowsTheSignedOutNotice()
    {
        (AccountNotice notice, _, OutboxSyncService sync, FakeHttpHandler handler, OutboxStore outbox) = Create();
        await outbox.AddAsync(StorageKeys.UserOutbox(Owner), AnyAnswer());
        handler.Respond = r => r.Path.EndsWith("/batch", StringComparison.Ordinal) ? FakeHttpHandler.Error(HttpStatusCode.Unauthorized, ApiErrorCodes.SignInRequired) : null;

        sync.Start(Owner);
        await sync.CurrentAttempt;

        Assert.True(notice.IsShown);
        Assert.Equal(AccountNoticeReason.SignedOut, notice.Reason);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, AccountNoticeReason.AccountChanged)]
    [InlineData(HttpStatusCode.Unauthorized, AccountNoticeReason.SignedOut)]
    public async Task SyncStoppedBeforeTheNoticeExists_StartsShown(HttpStatusCode status, AccountNoticeReason reason)
    {
        FakeTimeProvider time = new();
        FakeBrowserStore store = new();
        OutboxStore outbox = new(store);
        FakeHttpHandler handler = new()
        {
            Respond = r => r.Path.EndsWith("/batch", StringComparison.Ordinal)
                ? FakeHttpHandler.Error(status, status == HttpStatusCode.Conflict ? ApiErrorCodes.OwnerMismatch : ApiErrorCodes.SignInRequired)
                : null,
        };
        HttpClient http = handler.Client();
        AccountWatcher watcher = new(new FakeJsRuntime());
        OutboxSyncService sync = new(
            outbox, new SummaryStore(store), new TrainerApiClient(http, new AntiforgeryTokenProvider(http), new ServerClock(time, store), time), time, watcher);
        await outbox.AddAsync(StorageKeys.UserOutbox(Owner), AnyAnswer());
        sync.Start(Owner);
        await sync.CurrentAttempt;

        AccountNotice notice = new(watcher, sync);

        Assert.True(notice.IsShown);
        Assert.Equal(reason, notice.Reason);
    }

    [Fact]
    public async Task AccountChangeAfterSignedOut_BecomesAccountChanged()
    {
        (AccountNotice notice, AccountWatcher watcher, OutboxSyncService sync, FakeHttpHandler handler, OutboxStore outbox) = Create();
        await watcher.StartAsync("42");
        await outbox.AddAsync(StorageKeys.UserOutbox(Owner), AnyAnswer());
        handler.Respond = r => r.Path.EndsWith("/batch", StringComparison.Ordinal) ? FakeHttpHandler.Error(HttpStatusCode.Unauthorized, ApiErrorCodes.SignInRequired) : null;
        sync.Start(Owner);
        await sync.CurrentAttempt;
        int changes = 0;
        notice.Changed += () => changes++;

        watcher.OnAccountPublished("7");

        Assert.Equal(AccountNoticeReason.AccountChanged, notice.Reason);
        Assert.Equal(1, changes);
    }
}
