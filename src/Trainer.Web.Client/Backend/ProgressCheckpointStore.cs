using System.Text.Json;
using Trainer.Web.Client.Storage;

namespace Trainer.Web.Client.Backend;

/// <summary>
/// Reads and writes the <see cref="ProgressCheckpoint"/> of the anonymous answer log. Nothing here is authoritative: a read
/// returns null for anything that is not a checkpoint of exactly the log state asked for, and a failed write is only a
/// slower next page load, so neither raises.
/// </summary>
public sealed class ProgressCheckpointStore(IBrowserStore store)
{
    /// <summary>The fingerprint of the anonymous answer log, or null when it cannot be told without reading the log.</summary>
    public ValueTask<string?> FingerprintAsync() => store.FingerprintAsync(StorageKeys.AnonymousOutbox);

    /// <summary>The progress stored for the log state <paramref name="fingerprint"/>; null when there is none, it is for another state or it is damaged.</summary>
    public async ValueTask<LocalProgress?> ReadAsync(string fingerprint)
    {
        ArgumentException.ThrowIfNullOrEmpty(fingerprint);
        string? raw = await store.ReadAsync(StorageKeys.AnonymousProgressCheckpoint);
        if (raw is null)
        {
            return null;
        }

        try
        {
            return LocalProgress.FromCheckpoint(JsonSerializer.Deserialize(raw, ProgressCheckpointJsonContext.Default.ProgressCheckpoint), fingerprint);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes the checkpoint to free its share of the origin's quota for an answer; true when one was stored. It is derived
    /// data, so nothing is lost: the next load replays the log and writes it again.
    /// </summary>
    public async ValueTask<bool> EvictAsync()
    {
        StoreResult result = await store.MutateAsync(StoreOp.Delete(StorageKeys.AnonymousProgressCheckpoint));
        return result is { Ok: true, Count: > 0 };
    }

    /// <summary>Stores <paramref name="progress"/> as the checkpoint of its log state; does nothing when that state is not known.</summary>
    public async ValueTask WriteAsync(LocalProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (progress.ToCheckpoint() is not { } checkpoint)
        {
            return;
        }

        JsonElement document = JsonSerializer.SerializeToElement(checkpoint, ProgressCheckpointJsonContext.Default.ProgressCheckpoint);
        _ = await store.MutateAsync(StoreOp.Put(StorageKeys.AnonymousProgressCheckpoint, document));
    }
}
