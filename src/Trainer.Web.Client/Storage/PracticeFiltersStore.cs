using System.Text.Json;
using Trainer.Contracts;
using Trainer.Contracts.Storage;
using Trainer.Web.Client.Sessions;

namespace Trainer.Web.Client.Storage;

/// <summary>
/// The Practice filters last used to start a session (<see cref="StorageKeys.PracticeFilters"/>). A preference: a
/// failed write only means the next visit starts from the defaults, and an unreadable value is ignored.
/// </summary>
public sealed class PracticeFiltersStore(IBrowserStore store)
{
    public async ValueTask<StoredPracticeFilters?> LoadAsync()
    {
        StoredDocument document = StoredDocument.Parse(await store.ReadAsync(StorageKeys.PracticeFilters));
        if (document.Kind != StoredKind.Valid)
        {
            return null;
        }

        try
        {
            return document.Root.Deserialize(TrainerJsonContext.Default.StoredPracticeFilters);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async ValueTask SaveAsync(PracticeFilterSet filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        JsonElement value = JsonSerializer.SerializeToElement(filters.ToStored(), TrainerJsonContext.Default.StoredPracticeFilters);
        await store.MutateAsync(StoreOp.Put(StorageKeys.PracticeFilters, value));
    }
}
