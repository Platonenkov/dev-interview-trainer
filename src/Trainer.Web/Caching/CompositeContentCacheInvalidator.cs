using Trainer.Application.Abstractions;
using Trainer.Web.OpenGraph;

namespace Trainer.Web.Caching;

/// <summary>
/// The one <see cref="IContentCacheInvalidator"/> of the web host: the page cache and snapshot first, then the Open
/// Graph images. Each part runs even when the other failed, and every failure is reported together.
/// </summary>
public sealed class CompositeContentCacheInvalidator(OutputCacheInvalidator pages, OgImageCache images) : IContentCacheInvalidator
{
    public async Task InvalidateAsync(CancellationToken ct)
    {
        List<Exception> failures = [];
        await RunAsync(pages, failures, ct);
        await RunAsync(images, failures, ct);

        if (failures.Count > 0)
        {
            throw new AggregateException("Invalidating the content caches failed.", failures);
        }
    }

    private static async Task RunAsync(IContentCacheInvalidator invalidator, List<Exception> failures, CancellationToken ct)
    {
        try
        {
            await invalidator.InvalidateAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            failures.Add(ex);
        }
    }
}
