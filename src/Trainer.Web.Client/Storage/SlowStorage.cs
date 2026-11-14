namespace Trainer.Web.Client.Storage;

/// <summary>
/// Bounds how long an island waits for one storage write. Every write runs under a Web Lock that may never be granted
/// (another tab stuck holding it), and JS interop has no timeout in WebAssembly, so an unbounded wait could freeze the
/// island. After <see cref="WriteTimeout"/> the caller carries on with a fallback while the write goes on in the
/// background (its data is still in the store's memory), and <see cref="IsSlow"/> says so until a write completes.
/// </summary>
public sealed class SlowStorage(TimeProvider time)
{
    /// <summary>How long an island waits for one storage step before it carries on without it.</summary>
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Raised when <see cref="IsSlow"/> changes.</summary>
    public event Action? Changed;

    /// <summary>A storage step took longer than <see cref="WriteTimeout"/> and is still running.</summary>
    public bool IsSlow { get; private set; }

    /// <summary>The result of <paramref name="work"/>, or <paramref name="onTimeout"/> when it takes longer than <see cref="WriteTimeout"/>.</summary>
    public async Task<T> WaitAsync<T>(Task<T> work, T onTimeout)
    {
        ArgumentNullException.ThrowIfNull(work);
        try
        {
            return await work.WaitAsync(WriteTimeout, time);
        }
        catch (TimeoutException)
        {
            SetSlow(true);
            _ = work.ContinueWith(_ => SetSlow(false), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return onTimeout;
        }
    }

    private void SetSlow(bool slow)
    {
        if (IsSlow != slow)
        {
            IsSlow = slow;
            Changed?.Invoke();
        }
    }
}
