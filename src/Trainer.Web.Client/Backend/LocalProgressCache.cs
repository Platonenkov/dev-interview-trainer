using Trainer.Contracts;
using Trainer.Domain.Scheduling;
using Trainer.Web.Client.Storage;

namespace Trainer.Web.Client.Backend;

/// <summary>
/// The replayed progress of this page (static edition), computed once from the anonymous answer log and memoised. A load
/// takes the cheapest way that is still exactly right, in this order:
/// <list type="number">
/// <item>the stored <see cref="ProgressCheckpoint"/> when it is for the log state <c>outbox.js</c> reports now (a few
/// milliseconds, whatever the length of the log);</item>
/// <item>otherwise a replay of the whole log, after which the checkpoint is written for the next load.</item>
/// </list>
/// An answer this tab writes is applied to the memo directly when it follows everything the memo has seen
/// (<see cref="OutboxStore.Appended"/>), and the checkpoint is written again; any other change of the log drops the memo
/// (this tab's removal or move, or <see cref="OutboxStore.ChangedElsewhere"/>), so the next call loads again. The answer log
/// stays the only source of truth: a checkpoint that does not match it is never used, and unsaved answers (held only in
/// memory) always force a replay because no stored checkpoint can contain them.
/// </summary>
public sealed class LocalProgressCache : IDisposable
{
    private readonly OutboxStore _outbox;
    private readonly ProgressCheckpointStore _checkpoints;
    private readonly IReviewScheduler _scheduler;
    private readonly TimeProvider _time;
    private readonly TimeSpan _checkpointDelay;
    private readonly LocalMockHistoryStore? _history;
    private Task<LocalProgress>? _memo;

    // At most one checkpoint write runs; a newer progress queued meanwhile replaces the waiting one.
    private LocalProgress? _toWrite;
    private bool _writing;

    // Counts the times the memo was dropped, so a load that started before a drop does not queue a checkpoint after it.
    private int _generation;

    // Appended is raised before KeyChanged for the same write; the first has already updated the memo.
    private bool _appendHandled;

    /// <summary>How long after an answer the checkpoint is written, so the answer's own screen is drawn first.</summary>
    public static readonly TimeSpan DefaultCheckpointDelay = TimeSpan.FromMilliseconds(1000);

    /// <param name="checkpointDelay">
    /// Wait before a checkpoint is written (<see cref="DefaultCheckpointDelay"/> when null): writing it costs time on a
    /// phone, and an answer streak should draw its feedback first and cost one write, not one per answer. A page left
    /// inside the wait leaves a checkpoint that no longer matches, which the next load notices and replays the log for.
    /// </param>
    /// <param name="history">
    /// The finished mocks of this device: a mock refused for quota evicts the checkpoint too (see <see cref="MakeRoomAsync"/>).
    /// </param>
    public LocalProgressCache(OutboxStore outbox, IBrowserStore store, IReviewScheduler scheduler, TimeProvider time, TimeSpan? checkpointDelay = null, LocalMockHistoryStore? history = null)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(time);
        _outbox = outbox;
        _checkpoints = new ProgressCheckpointStore(store);
        _scheduler = scheduler;
        _time = time;
        _checkpointDelay = checkpointDelay ?? DefaultCheckpointDelay;
        _history = history;
        _outbox.Appended += OnAppended;
        _outbox.KeyChanged += OnKeyChanged;
        _outbox.ChangedElsewhere += Invalidate;
        _outbox.MakeRoomAsync = MakeRoomAsync;
        if (history is not null)
        {
            history.MakeRoomAsync = MakeRoomAsync;
        }
    }

    /// <summary>
    /// The progress for the current log. The computation is shared by every caller of one generation, so a caller whose
    /// token is cancelled stops waiting without cancelling the others.
    /// </summary>
    public async ValueTask<LocalProgress> GetAsync(CancellationToken ct)
    {
        Task<LocalProgress> load = _memo ??= LoadAsync();
        try
        {
            return await load.WaitAsync(ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Not memoised: a failed read is tried again by the next call.
            if (ReferenceEquals(_memo, load))
            {
                _memo = null;
            }

            throw;
        }
    }

    public void Dispose()
    {
        _outbox.Appended -= OnAppended;
        _outbox.KeyChanged -= OnKeyChanged;
        _outbox.ChangedElsewhere -= Invalidate;
        if (_outbox.MakeRoomAsync == MakeRoomAsync)
        {
            _outbox.MakeRoomAsync = null;
        }

        if (_history is not null && _history.MakeRoomAsync == MakeRoomAsync)
        {
            _history.MakeRoomAsync = null;
        }
    }

    private void OnAppended(string key, IReadOnlyList<AnswerDto> answers, StoreResult stored)
    {
        if (!string.Equals(key, StorageKeys.AnonymousOutbox, StringComparison.Ordinal))
        {
            return;
        }

        // Only a memo that has finished can be extended; one still loading, or failed, is dropped like any other change.
        if (_memo is { IsCompletedSuccessfully: true } memo
            && memo.Result.Fingerprint is { } known
            && string.Equals(known, stored.Previous, StringComparison.Ordinal)
            && stored.Fingerprint is { } current
            && stored.Added == answers.Count
            && memo.Result.TryAppend(answers, _scheduler, current, out LocalProgress next))
        {
            // The memo described the log exactly as it was before this write, and the write only added these answers.
            _memo = Task.FromResult(next);
            _appendHandled = true;
            QueueCheckpoint(next);
        }
    }

    private void OnKeyChanged(string key)
    {
        if (!string.Equals(key, StorageKeys.AnonymousOutbox, StringComparison.Ordinal))
        {
            return;
        }

        if (_appendHandled)
        {
            _appendHandled = false;
            return;
        }

        DropMemo();
    }

    private void Invalidate() => DropMemo();

    // A checkpoint still waiting to be written describes the log as the dropped memo saw it. Another tab may have stored a
    // fresher one meanwhile, and a stale checkpoint written over it costs the next load a full replay.
    private void DropMemo()
    {
        _memo = null;
        _toWrite = null;
        _generation++;
    }

    private async ValueTask<bool> MakeRoomAsync(string key)
    {
        // The answer log and the mock history are the data that must not be lost; the checkpoint is derived from the log.
        if (!string.Equals(key, StorageKeys.AnonymousOutbox, StringComparison.Ordinal)
            && !string.Equals(key, StorageKeys.AnonymousMockHistory, StringComparison.Ordinal))
        {
            return false;
        }

        _toWrite = null;
        return await _checkpoints.EvictAsync();
    }

    private async Task<LocalProgress> LoadAsync()
    {
        int generation = _generation;
        // Listen first, then read: another tab's write in between still arrives as an event.
        await _outbox.WatchAsync(StorageKeys.AnonymousOutbox);
        string? fingerprint = await _checkpoints.FingerprintAsync();
        if (fingerprint is not null && _outbox.UnsavedCount == 0 && await _checkpoints.ReadAsync(fingerprint) is { } stored)
        {
            return stored;
        }

        // A replay covers the log as it was read; the fingerprint is taken on both sides of the read, and the result is
        // only tied to a log state (and stored) when no other tab wrote in between.
        IReadOnlyList<AnswerDto> log = await _outbox.ReadAsync(StorageKeys.AnonymousOutbox);
        LocalProgress replayed = LocalProgress.Replay(log, _scheduler, _time.GetUtcNow());
        string? after = fingerprint is null ? null : await _checkpoints.FingerprintAsync();
        if (fingerprint is null || !string.Equals(fingerprint, after, StringComparison.Ordinal) || _outbox.UnsavedCount > 0)
        {
            return replayed;
        }

        LocalProgress known = replayed.WithFingerprint(fingerprint);
        // The memo may have been dropped while the log was read: then the progress is right for what was read, but another
        // tab has written since and a checkpoint of it would be stale from the first moment.
        if (generation == _generation)
        {
            QueueCheckpoint(known);
        }

        return known;
    }

    private void QueueCheckpoint(LocalProgress progress)
    {
        _toWrite = progress;
        if (_writing)
        {
            return;
        }

        _writing = true;
        _ = DrainCheckpointsAsync();
    }

    private async Task DrainCheckpointsAsync()
    {
        try
        {
            // The answer or the page load that queued this is not made to wait for the write, and answers that follow within
            // the wait replace the progress waiting here, so a streak costs one write.
            if (_checkpointDelay > TimeSpan.Zero)
            {
                await Task.Delay(_checkpointDelay);
            }
            else
            {
                await Task.Yield();
            }

            while (_toWrite is { } progress)
            {
                _toWrite = null;
                await _checkpoints.WriteAsync(progress);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A checkpoint only saves time; the next load replays the log if this one did not reach storage.
            _toWrite = null;
        }
        finally
        {
            _writing = false;
        }
    }
}
