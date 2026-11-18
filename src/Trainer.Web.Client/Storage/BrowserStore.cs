using System.Text.Json;
using Microsoft.JSInterop;

namespace Trainer.Web.Client.Storage;

/// <summary><see cref="IBrowserStore"/> over <c>window.ditStore</c>; both directions are JSON strings through source-generated metadata.</summary>
public sealed class BrowserStore(IJSRuntime js) : IBrowserStore
{
    public async ValueTask<StoreResult> MutateAsync(StoreOp op)
    {
        string request = JsonSerializer.Serialize(op, StorageJsonContext.Default.StoreOp);
        try
        {
            string response = await js.InvokeAsync<string>("ditStore.mutate", request);
            return JsonSerializer.Deserialize(response, StorageJsonContext.Default.StoreResult)
                ?? StoreResult.Failed(StoreErrors.Unavailable);
        }
        catch (JSException)
        {
            return StoreResult.Failed(StoreErrors.Unavailable);
        }
        catch (JsonException)
        {
            return StoreResult.Failed(StoreErrors.Unavailable);
        }
    }

    public async ValueTask<string?> ReadAsync(string key) => (await ReadCheckedAsync(key)).Value;

    public async ValueTask<StoredRead> ReadCheckedAsync(string key)
    {
        try
        {
            return new StoredRead(true, await js.InvokeAsync<string?>("ditStore.read", key));
        }
        catch (JSException)
        {
            return new StoredRead(false, null);
        }
    }

    public async ValueTask<string?> FingerprintAsync(string key)
    {
        try
        {
            return await js.InvokeAsync<string?>("ditStore.fingerprint", key);
        }
        catch (JSException)
        {
            return null;
        }
    }

    public async ValueTask<IAsyncDisposable> WatchKeyAsync(string key, Action onChanged)
    {
        KeyWatch watch = new(js, onChanged);
        await watch.StartAsync(key);
        return watch;
    }

    /// <summary>One <c>ditStore.watchKey</c> registration; <c>outbox.js</c> calls <see cref="OnKeyChanged"/> for each event.</summary>
    public sealed class KeyWatch(IJSRuntime js, Action onChanged) : IAsyncDisposable
    {
        private DotNetObjectReference<KeyWatch>? _reference;
        private int? _handle;

        internal async ValueTask StartAsync(string key)
        {
            _reference = DotNetObjectReference.Create(this);
            try
            {
                _handle = await js.InvokeAsync<int>("ditStore.watchKey", key, _reference);
            }
            catch (JSException)
            {
                // No script or no storage: there is no other tab to hear from.
            }
        }

        [JSInvokable]
        public void OnKeyChanged() => onChanged();

        public async ValueTask DisposeAsync()
        {
            if (_handle is { } handle)
            {
                _handle = null;
                try
                {
                    await js.InvokeVoidAsync("ditStore.unwatchKey", handle);
                }
                catch (JSDisconnectedException)
                {
                    // The page is going away; its listeners go with it.
                }
                catch (JSException)
                {
                    // Nothing left to unregister.
                }
            }

            _reference?.Dispose();
            _reference = null;
        }
    }
}
