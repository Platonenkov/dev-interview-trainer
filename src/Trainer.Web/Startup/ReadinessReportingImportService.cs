using Trainer.Application.Content;
using Trainer.Web.ContentState;

namespace Trainer.Web.Startup;

/// <summary>
/// Wraps the import service so that every successful import in this process, not only the startup one, returns
/// readiness to <see cref="ReadinessStatus.Ready"/>. Without it a host whose startup import failed would stay red
/// even after a later import has stored good content. Readiness is reported only after the content snapshot has been
/// reloaded, because routing answers 404 for every stack while the snapshot is still empty. The reload happens here
/// even for <see cref="ContentImportStatus.Imported"/>, where the cache invalidator also refreshes: that invalidator
/// swallows failures, and a swallowed failure must not leave the host reporting Ready on a stale snapshot.
/// </summary>
public sealed partial class ReadinessReportingImportService(
    IContentImportService inner,
    ReadinessState readiness,
    ContentSnapshotProvider snapshots,
    ILogger<ReadinessReportingImportService> logger) : IContentImportService
{
    public async Task<ContentImportResult> ImportAsync(string contentRoot, CancellationToken ct)
    {
        ContentImportResult result = await inner.ImportAsync(contentRoot, ct);
        if (result.Status is not (ContentImportStatus.Imported or ContentImportStatus.Unchanged))
        {
            return result;
        }

        try
        {
            await snapshots.RefreshAsync(ct);
            readiness.MarkReady();
        }
        catch (Exception ex) when (result.Status == ContentImportStatus.Imported && (ex is not OperationCanceledException || !ct.IsCancellationRequested))
        {
            // The content is committed, so the import itself succeeded; the host just must not claim readiness on a stale
            // snapshot. The startup importer sees a state that is not Ready and reloads the snapshot again.
            LogRefreshFailed(ex);
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Content was imported, but reloading the content snapshot failed; readiness is not reported.")]
    private partial void LogRefreshFailed(Exception ex);
}
