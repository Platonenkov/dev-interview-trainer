using Trainer.Web.Client.Storage;

namespace Trainer.Web.Client.Sync;

public enum ImportStatus
{
    /// <summary>The anonymous key is empty and the answers reached the server.</summary>
    Imported,

    /// <summary>The anonymous key is empty; the answers wait in the user's outbox for the next flush.</summary>
    Queued,

    /// <summary>Answers are still under the anonymous key (a write failed); trying again finishes the move.</summary>
    Incomplete,

    /// <summary>Another tab changed the account or syncing stopped: this page must not decide for the account.</summary>
    Refused,
}

/// <param name="Moved">
/// Answers that left the anonymous key in this attempt, including ones another tab of the same account moved between
/// this page's reading the key and its own move (they are imported all the same). Ones another tab discarded in that
/// window are not counted.
/// </param>
/// <param name="StillHere">Answers still under the anonymous key afterwards; null when the key could not be read after the
/// move (or holds something this app cannot list), so the count is unknown.</param>
public readonly record struct ImportResult(ImportStatus Status, int Moved, int? StillHere);

public enum DiscardResult
{
    Discarded,

    /// <summary>Some of the counted answers are still stored: the write failed.</summary>
    Failed,

    Refused,
}

/// <summary>
/// The anonymous import (spec 4, 11.4): answers this browser kept before sign-in stay under
/// <see cref="StorageKeys.AnonymousOutbox"/> until the user chooses. Import moves them into the signed-in owner's outbox
/// (<see cref="OutboxStore.MoveAsync"/>) and then flushes like any answer; the server deduplicates by client answer id
/// (spec 8.8). Success is judged by reading the anonymous key itself afterwards, never from the move's count. Nothing is
/// done once another tab changed the account (<see cref="AccountWatcher"/>) or syncing stopped: such a page is told to
/// reload, and it must not move answers into an account that is no longer signed in here.
/// </summary>
public sealed class AnonymousImport(OutboxStore outbox, IBrowserStore store, OutboxSyncService sync, AccountWatcher accounts)
{
    private const string AnswersMember = "answers";
    private const string IdMember = "clientAnswerId";

    /// <summary>False once another tab changed the account or syncing stopped for good.</summary>
    public bool CanAct => accounts.ChangedTo is null && !sync.IsStopped;

    /// <summary>
    /// The ids stored under the anonymous key, read directly (no store op, so no quarantine write and nothing hidden by
    /// a removal still in flight).
    /// </summary>
    public async Task<IReadOnlyList<string>> PendingIdsAsync() => (await CheckedPendingIdsAsync()).Ids;

    // Known is false when the key could not be read or holds something this app cannot list (corrupt, a newer version):
    // answers may still be there, so an empty list must not be taken for "nothing left".
    private async Task<(bool Known, IReadOnlyList<string> Ids)> CheckedPendingIdsAsync()
    {
        StoredRead read = await store.ReadCheckedAsync(StorageKeys.AnonymousOutbox);
        StoredDocument document = StoredDocument.Parse(read.Value);
        return document.Kind switch
        {
            StoredKind.Valid => (read.Readable, document.Ids(AnswersMember, IdMember)),
            StoredKind.Missing => (read.Readable, []),
            _ => (false, []),
        };
    }

    /// <summary>Moves the anonymous answers into <paramref name="owner"/>'s outbox and flushes it.</summary>
    /// <param name="flushWait">How long to wait for the server before reporting the answers as queued.</param>
    public async Task<ImportResult> ImportAsync(long owner, TimeSpan flushWait, CancellationToken ct)
    {
        if (!CanAct)
        {
            return new ImportResult(ImportStatus.Refused, 0, (await PendingIdsAsync()).Count);
        }

        IReadOnlyList<string> before = await PendingIdsAsync();
        MoveOutcome outcome = await outbox.MoveAsync(StorageKeys.AnonymousOutbox, StorageKeys.UserOutbox(owner));
        (bool known, IReadOnlyList<string> remaining) = await CheckedPendingIdsAsync();
        HashSet<string> after = new(remaining, StringComparer.Ordinal);
        // An id that left the anonymous key counts only if it is now in the owner's outbox: one another tab discarded in
        // the same window is gone, not imported.
        HashSet<string> owned = new((await outbox.ReadAsync(StorageKeys.UserOutbox(owner))).Select(a => a.ClientAnswerId), StringComparer.Ordinal);
        int moved = known ? Math.Max(outcome.Moved, before.Count(id => !after.Contains(id) && owned.Contains(id))) : outcome.Moved;
        if (!known || after.Count > 0 || outcome.SourcePending > 0)
        {
            // The copies the move wrote sit in the owner's outbox (the server deduplicates them against the originals still
            // under the anonymous key); send them now rather than at the next page load. An unreadable key may still hold
            // every answer it held before.
            sync.RequestFlush();
            int? stillHere = known ? Math.Max(after.Count, outcome.SourcePending) : null;
            return new ImportResult(ImportStatus.Incomplete, moved, stillHere);
        }

        bool synced = await sync.FlushAsync(flushWait, ct);
        return new ImportResult(synced ? ImportStatus.Imported : ImportStatus.Queued, moved, 0);
    }

    /// <summary>Deletes exactly <paramref name="counted"/>, the answers the user confirmed; ones added since stay.</summary>
    public async Task<DiscardResult> DiscardAsync(IReadOnlyCollection<string> counted)
    {
        ArgumentNullException.ThrowIfNull(counted);
        if (!CanAct)
        {
            return DiscardResult.Refused;
        }

        await outbox.RemoveAsync(StorageKeys.AnonymousOutbox, counted);
        (bool known, IReadOnlyList<string> remaining) = await CheckedPendingIdsAsync();
        HashSet<string> left = new(remaining, StringComparer.Ordinal);
        return !known || counted.Any(left.Contains) ? DiscardResult.Failed : DiscardResult.Discarded;
    }
}
