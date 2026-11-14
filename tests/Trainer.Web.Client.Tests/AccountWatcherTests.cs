using Trainer.Web.Client.Sync;

namespace Trainer.Web.Client.Tests;

public sealed class AccountWatcherTests
{
    [Fact]
    public async Task Watcher_PublishesOwnerAndRaisesOnceForAnotherOwner()
    {
        FakeJsRuntime js = new();
        await using AccountWatcher watcher = new(js);
        List<string> changes = [];
        watcher.Changed += changes.Add;

        await watcher.StartAsync("42");
        watcher.OnAccountPublished("42");
        watcher.OnAccountPublished("anon");
        watcher.OnAccountPublished("7");

        Assert.Equal("ditStore.watchAccount", js.Calls[0].Identifier);
        Assert.Equal("ditStore.claimAccount", js.Calls[1].Identifier);
        Assert.Equal("42", js.Calls[1].Args![0]);
        Assert.Equal(["anon"], changes);
        Assert.Equal("anon", watcher.ChangedTo);
    }

    [Fact]
    public async Task Watcher_DisposeUnwatches()
    {
        FakeJsRuntime js = new();
        AccountWatcher watcher = new(js);
        await watcher.StartAsync("anon");

        await watcher.DisposeAsync();

        Assert.Equal(("ditStore.unwatchAccount", FakeJsRuntime.WatchHandle), (js.Calls[^1].Identifier, js.Calls[^1].Args![0]));
    }

    [Fact]
    public async Task Watcher_WithoutScript_StartsQuietlyAndDisposesWithoutUnwatch()
    {
        FakeJsRuntime js = new() { Throw = true };
        AccountWatcher watcher = new(js);

        await watcher.StartAsync("42");
        watcher.OnAccountPublished("42");
        await watcher.DisposeAsync();

        Assert.Null(watcher.ChangedTo);
        Assert.DoesNotContain(js.Calls, c => c.Identifier == "ditStore.unwatchAccount");
    }

    [Fact]
    public async Task Watcher_AnotherOwnerPublishedSinceThePageLoaded_IsAChange()
    {
        FakeJsRuntime js = new() { ClaimResult = "7" };
        await using AccountWatcher watcher = new(js);
        List<string> changes = [];
        watcher.Changed += changes.Add;

        await watcher.StartAsync("42");

        Assert.Equal("7", watcher.ChangedTo);
        Assert.Equal(["7"], changes);
        // Listening starts before the claim, so a change between the two cannot slip through either.
        Assert.Equal(["ditStore.watchAccount", "ditStore.claimAccount"], js.Calls.Select(c => c.Identifier));
        Assert.DoesNotContain(js.Calls, c => c.Identifier == "ditStore.publishAccount");
    }
}
