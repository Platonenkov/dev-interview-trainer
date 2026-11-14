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
}
