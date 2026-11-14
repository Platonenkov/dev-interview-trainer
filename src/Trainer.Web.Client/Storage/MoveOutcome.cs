namespace Trainer.Web.Client.Storage;

/// <summary>The result of <see cref="OutboxStore.MoveAsync"/>.</summary>
/// <param name="Moved">Entries that are now in the destination and no longer in the source.</param>
/// <param name="SourcePending">
/// Entries already written to the destination that are still in the source because its rewrite failed; a later move from
/// the same key retries their removal.
/// </param>
public readonly record struct MoveOutcome(int Moved, int SourcePending);
