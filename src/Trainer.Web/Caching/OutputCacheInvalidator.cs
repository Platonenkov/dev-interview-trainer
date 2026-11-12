using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.OutputCaching;
using Trainer.Application.Abstractions;
using Trainer.Web.ContentState;

namespace Trainer.Web.Caching;

/// <summary>
/// After an import: publishes a new <see cref="ContentSnapshot"/> generation first, then evicts everything tagged
/// <see cref="ContentTag"/>, so a page rendered after the eviction already sees the new taxonomy. The eviction runs
/// even when the snapshot refresh throws.
/// </summary>
public sealed class OutputCacheInvalidator(ContentSnapshotProvider snapshots, IOutputCacheStore store) : IContentCacheInvalidator
{
    public const string ContentTag = "content";

    public async Task InvalidateAsync(CancellationToken ct)
    {
        Exception? refreshFailure = null;
        try
        {
            await snapshots.RefreshAsync(ct);
        }
        catch (Exception ex)
        {
            refreshFailure = ex;
        }

        try
        {
            // Stale pages must go even when the refresh failed. The eviction ignores the caller's token for the same
            // reason: it is an in-memory operation and must not be skipped.
            await store.EvictByTagAsync(ContentTag, CancellationToken.None);
        }
        catch (Exception evictionFailure) when (refreshFailure is not null)
        {
            throw new AggregateException("Refreshing the content snapshot and evicting the output cache both failed.", refreshFailure, evictionFailure);
        }

        if (refreshFailure is not null)
        {
            ExceptionDispatchInfo.Capture(refreshFailure).Throw();
        }
    }
}
