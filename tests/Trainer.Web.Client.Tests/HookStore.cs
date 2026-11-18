using Trainer.Web.Client.Storage;

namespace Trainer.Web.Client.Tests;

/// <summary>Lets a test answer, delay or interleave single ops; everything else goes to the fake.</summary>
internal sealed class HookStore(FakeBrowserStore inner) : IBrowserStore
{
    private readonly Dictionary<string, List<Action>> _watchers = new(StringComparer.Ordinal);

    public FakeBrowserStore Inner => inner;

    /// <summary>Returns a result to answer the op without running it, or null to run it.</summary>
    public Func<StoreOp, ValueTask<StoreResult?>>? Intercept { get; set; }

    /// <summary>Awaited before a read answers, so a test can hold a read back.</summary>
    public Func<string, Task>? ReadGate { get; set; }

    /// <summary>Every key read, in order.</summary>
    public List<string> Reads { get; } = [];

    public async ValueTask<string?> ReadAsync(string key)
    {
        Reads.Add(key);
        if (ReadGate is { } gate)
        {
            await gate(key);
        }

        return await inner.ReadAsync(key);
    }

    public ValueTask<string?> FingerprintAsync(string key) => inner.FingerprintAsync(key);

    public async ValueTask<StoreResult> MutateAsync(StoreOp op)
    {
        if (Intercept is { } intercept && await intercept(op) is { } answer)
        {
            return answer;
        }

        return await inner.MutateAsync(op);
    }

    public ValueTask<IAsyncDisposable> WatchKeyAsync(string key, Action onChanged)
    {
        if (!_watchers.TryGetValue(key, out List<Action>? list))
        {
            list = [];
            _watchers[key] = list;
        }

        list.Add(onChanged);
        return ValueTask.FromResult<IAsyncDisposable>(new Registration(list, onChanged));
    }

    /// <summary>Acts as the browser's <c>storage</c> event for a write another tab made to <paramref name="key"/>.</summary>
    public void RaiseChangedElsewhere(string key)
    {
        if (_watchers.TryGetValue(key, out List<Action>? list))
        {
            foreach (Action callback in list.ToArray())
            {
                callback();
            }
        }
    }

    /// <summary>The number of live listeners on <paramref name="key"/>.</summary>
    public int WatcherCount(string key) => _watchers.TryGetValue(key, out List<Action>? list) ? list.Count : 0;

    private sealed class Registration(List<Action> list, Action callback) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            list.Remove(callback);
            return ValueTask.CompletedTask;
        }
    }
}
