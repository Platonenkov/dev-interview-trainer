using Microsoft.Extensions.Time.Testing;
using Trainer.Web.Client.Storage;

namespace Trainer.Web.Client.Tests;

public sealed class SlowStorageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Wait_WriteInTime_ReturnsItsResult_NotSlow()
    {
        SlowStorage slow = new(new FakeTimeProvider());

        bool result = await slow.WaitAsync(Task.FromResult(true), false);

        Assert.True(result);
        Assert.False(slow.IsSlow);
    }

    [Fact]
    public async Task Wait_WriteNeverReturns_GivesFallbackAfterTimeout_AndReportsSlowUntilItEnds()
    {
        FakeTimeProvider time = new();
        SlowStorage slow = new(time);
        int changes = 0;
        slow.Changed += () => changes++;
        TaskCompletionSource<bool> write = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<bool> waiting = slow.WaitAsync(write.Task, false);
        Assert.False(waiting.IsCompleted);
        time.Advance(SlowStorage.WriteTimeout);
        bool result = await waiting.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.False(result);
        Assert.True(slow.IsSlow);
        Assert.Equal(1, changes);

        write.SetResult(true);
        await WaitUntilAsync(() => !slow.IsSlow);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void WriteTimeout_IsFiveSeconds() => Assert.Equal(TimeSpan.FromSeconds(5), SlowStorage.WriteTimeout);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }

        Assert.True(condition());
    }
}
