namespace Trainer.Application.Content;

public interface IContentStatusQueries
{
    /// <summary>The hash of the active import (the <c>content_imports</c> row with <c>is_active</c>), or null when nothing was imported yet.</summary>
    Task<string?> GetActiveHashAsync(CancellationToken ct);
}
