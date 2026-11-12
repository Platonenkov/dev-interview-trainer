namespace Trainer.Application.Abstractions;

/// <summary>Drops cached content-derived data after a successful import; each host supplies its own implementation.</summary>
public interface IContentCacheInvalidator
{
    Task InvalidateAsync(CancellationToken ct);
}
