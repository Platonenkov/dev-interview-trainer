using Microsoft.Extensions.Options;
using Trainer.Application.Content;
using Trainer.Web.ContentState;

namespace Trainer.Web.Startup;

/// <summary>
/// Imports the content once at startup without blocking the host from listening. Transient database failures are retried
/// with exponential backoff, because on a fresh deployment the app often starts before PostgreSQL accepts connections.
/// A rejected or non-transient failure leaves the readiness probe red but still loads whatever the database already
/// holds, so the previous content keeps serving. After the backoff attempts a transient failure does the same, and the
/// importer then keeps retrying at the maximum delay until it succeeds or the host stops.
/// </summary>
public sealed partial class ContentStartupImporter(
    IServiceScopeFactory scopeFactory,
    ContentSnapshotProvider snapshots,
    ReadinessState readiness,
    IOptions<AppOptions> options,
    IHostEnvironment environment,
    TimeProvider time,
    ILogger<ContentStartupImporter> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // BackgroundService runs this method synchronously up to its first await, so without a yield any blocking
        // work in the reader would delay host start.
        await Task.Yield();

        StartupImportOptions retry = options.Value.StartupImport;
        int maxAttempts = Math.Max(1, retry.MaxAttempts);

        try
        {
            bool exhausted = false;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await ImportOnceAsync(stoppingToken);
                    return;
                }
                catch (Exception ex) when (TransientFailure.IsTransient(ex) && !stoppingToken.IsCancellationRequested)
                {
                    // A database that stays down is an outage, not a reason to leave the process without content until
                    // someone restarts it: after the backoff attempts the importer keeps trying at the maximum delay.
                    TimeSpan delay = attempt < maxAttempts ? retry.DelayAfter(attempt) : retry.MaxDelay;
                    if (attempt >= maxAttempts && !exhausted)
                    {
                        exhausted = true;
                        LogFailed(ex);
                        readiness.MarkFailed();
                        await TryRefreshSnapshotAsync(stoppingToken);
                    }

                    LogRetrying(ex, attempt, maxAttempts, delay);
                    await Task.Delay(delay, time, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogFailed(ex);
            readiness.MarkFailed();
            await TryRefreshSnapshotAsync(stoppingToken);
        }
    }

    private async Task ImportOnceAsync(CancellationToken ct)
    {
        ContentImportResult result = await ImportAsync(ct);

        // The import service reloads the snapshot itself and reports Ready only afterwards. A rejected import has not, and
        // a failed reload leaves the state not Ready, so both are covered here (this refresh throws if it fails again).
        if (result.Status == ContentImportStatus.Rejected || readiness.Status != ReadinessStatus.Ready)
        {
            await snapshots.RefreshAsync(ct);
        }

        if (result.Status == ContentImportStatus.Rejected)
        {
            LogRejected(result);
            readiness.MarkFailed();
            return;
        }

        LogFinished(result.Status, result.Hash);
        readiness.MarkReady();
    }

    private async Task<ContentImportResult> ImportAsync(CancellationToken ct)
    {
        string contentRoot = options.Value.ContentRoot
            ?? throw new InvalidOperationException("App:ContentRoot is not configured.");

        // A relative root (appsettings.Development.json) is relative to the app, not to wherever the process was started.
        string root = Path.GetFullPath(contentRoot, environment.ContentRootPath);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IContentImportService importer = scope.ServiceProvider.GetRequiredService<IContentImportService>();
        return await importer.ImportAsync(root, ct);
    }

    private void LogRejected(ContentImportResult result)
    {
        foreach (ImportIssue issue in result.Issues)
        {
            LogIssue(issue.Path, issue.Code, issue.Message);
        }
    }

    private async Task TryRefreshSnapshotAsync(CancellationToken ct)
    {
        try
        {
            await snapshots.RefreshAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSnapshotFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Content import finished: {Status}, hash {Hash}.")]
    private partial void LogFinished(ContentImportStatus status, string hash);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Startup content import failed on attempt {Attempt} (backoff attempts: {MaxAttempts}) with a transient error; retrying in {Delay}.")]
    private partial void LogRetrying(Exception ex, int attempt, int maxAttempts, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Error, Message = "Startup content import failed.")]
    private partial void LogFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Content rejected: {Path} [{Code}] {Message}")]
    private partial void LogIssue(string path, string code, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not load the stored content taxonomy.")]
    private partial void LogSnapshotFailed(Exception ex);
}
