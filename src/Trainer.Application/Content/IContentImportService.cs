namespace Trainer.Application.Content;

public interface IContentImportService
{
    /// <summary>
    /// Reads, validates and stores the content under <paramref name="contentRoot"/> in one transaction.
    /// Concurrent calls (also from other processes) are serialised by a database lock.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException"><paramref name="contentRoot"/> does not exist.</exception>
    Task<ContentImportResult> ImportAsync(string contentRoot, CancellationToken ct);
}
