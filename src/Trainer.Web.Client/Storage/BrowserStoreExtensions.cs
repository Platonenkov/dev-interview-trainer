namespace Trainer.Web.Client.Storage;

/// <summary>The quarantine rule every store shares: an unparseable value is set aside once, then the work goes on.</summary>
internal static class BrowserStoreExtensions
{
    /// <summary>
    /// Reads and classifies a key. A corrupt value is quarantined (copied, then removed) and the key read again; if the
    /// copy fails the value stays in place and the result stays <see cref="StoredKind.Corrupt"/>.
    /// </summary>
    public static async ValueTask<StoredDocument> ReadDocumentAsync(this IBrowserStore store, string key)
    {
        StoredDocument document = StoredDocument.Parse(await store.ReadAsync(key));
        if (document.Kind == StoredKind.Corrupt && (await store.MutateAsync(StoreOp.Quarantine(key))).Ok)
        {
            document = StoredDocument.Parse(await store.ReadAsync(key));
        }

        return document;
    }

    /// <summary>Runs an op; when it hits a corrupt value, quarantines the keys it touches and runs it once more.</summary>
    public static async ValueTask<StoreResult> MutateWithQuarantineAsync(this IBrowserStore store, StoreOp op)
    {
        StoreResult result = await store.MutateAsync(op);
        if (!string.Equals(result.Error, StoreErrors.Corrupt, StringComparison.Ordinal))
        {
            return result;
        }

        bool cleared = (await store.MutateAsync(StoreOp.Quarantine(op.Key))).Ok;
        if (op.To is not null)
        {
            cleared &= (await store.MutateAsync(StoreOp.Quarantine(op.To))).Ok;
        }

        return cleared ? await store.MutateAsync(op) : result;
    }
}
