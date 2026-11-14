namespace Trainer.Web.Client.Storage;

/// <summary>
/// The keys a store found written by a newer app version. Those keys are never touched; the store reports them so
/// <c>StorageNotice</c> can ask for a reload.
/// </summary>
internal sealed class NewerVersionKeys
{
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    public bool Any => _keys.Count > 0;

    /// <summary>Records what a read found; returns true when that changed <see cref="Any"/>.</summary>
    public bool Note(string key, StoredKind kind) => Set(key, kind == StoredKind.NewerVersion, kind != StoredKind.Corrupt);

    /// <summary>Records what an op reported; returns true when that changed <see cref="Any"/>.</summary>
    public bool Note(string key, StoreResult result) =>
        Set(key, string.Equals(result.Error, StoreErrors.NewerVersion, StringComparison.Ordinal), result.Ok);

    private bool Set(string key, bool newer, bool known)
    {
        bool before = Any;
        if (newer)
        {
            _keys.Add(key);
        }
        else if (known)
        {
            _keys.Remove(key);
        }

        return before != Any;
    }
}
