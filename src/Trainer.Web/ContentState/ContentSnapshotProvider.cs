using Trainer.Application.Stacks;
using Trainer.Domain.Keys;

namespace Trainer.Web.ContentState;

/// <summary>
/// Holds the current <see cref="ContentSnapshot"/>. It is reloaded from the database after every startup import and
/// by the cache invalidator after every later one, and each reload publishes a new generation.
/// </summary>
public sealed class ContentSnapshotProvider(IServiceScopeFactory scopeFactory) : IDisposable
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private volatile ContentSnapshot _current = ContentSnapshot.Empty;

    public ContentSnapshot Current => _current;

    public async Task RefreshAsync(CancellationToken ct)
    {
        // Serialised so that a slower, older read cannot overwrite the result of a newer one.
        await _refreshLock.WaitAsync(ct);
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            IStackQueries queries = scope.ServiceProvider.GetRequiredService<IStackQueries>();
            IReadOnlyDictionary<StackKey, IReadOnlyList<TopicKey>> taxonomy = await queries.GetTaxonomyAsync(ct);
            _current = new ContentSnapshot(_current.Generation + 1, taxonomy);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Dispose() => _refreshLock.Dispose();
}
