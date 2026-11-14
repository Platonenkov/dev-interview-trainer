using System.Buffers;
using System.Text.Json;
using Trainer.Web.Client.Storage;

namespace Trainer.Web.Client.Api;

/// <summary>
/// The server's clock as seen from this device (ADR 0005). Every <c>/api</c> response's <c>Date</c> header calibrates
/// it; an answer takes <see cref="Now"/> as its <c>answeredAt</c> when it is given and keeps it forever, so retries and
/// later device clock changes never redate it. Between calibrations the time runs on the monotonic timestamp
/// (<see cref="TimeProvider.GetTimestamp"/>) from the last anchor, so a user or NTP moving the device's wall clock
/// mid-session cannot move the stamps.
/// </summary>
public sealed class ServerClock(TimeProvider device, IBrowserStore store)
{
    /// <summary>A slower round trip says too little about when the server wrote its <c>Date</c>.</summary>
    public static readonly TimeSpan MaxRoundTrip = TimeSpan.FromSeconds(10);

    /// <summary>A larger offset is a broken header, not a wrong device clock.</summary>
    public static readonly TimeSpan MaxOffset = TimeSpan.FromDays(365);

    /// <summary>
    /// An older persisted offset is not trusted: the device clock may have been corrected since, and a stale offset
    /// would stamp answers the server rejects as in the future.
    /// </summary>
    public static readonly TimeSpan MaxPersistedAge = TimeSpan.FromHours(1);

    private const int DocumentVersion = 1;
    private const string OffsetMember = "offsetMs";
    private const string ObservedAtMember = "observedAt";

    // Date has a resolution of one second and is truncated, so the true server time is half a second later on average.
    private static readonly TimeSpan DateResolutionHalf = TimeSpan.FromMilliseconds(500);

    // The server time at the anchor and the device timestamp taken at that moment; no anchor before the first calibration.
    private DateTimeOffset? _anchorServerTime;
    private long _anchorTimestamp;

    // Persistence is one writer at a time that always writes the newest sample: two overlapping fire-and-forget writes
    // could finish out of order and leave an older offset in storage.
    private readonly object _persistLock = new();
    private (TimeSpan Offset, DateTimeOffset ObservedAt)? _pendingWrite;
    private bool _writing;

    /// <summary>Server time minus the device's wall clock right now; zero before any calibration.</summary>
    public TimeSpan Offset => Now() - device.GetUtcNow();

    public DateTimeOffset Now() =>
        _anchorServerTime is { } anchor ? anchor + device.GetElapsedTime(_anchorTimestamp) : device.GetUtcNow();

    /// <summary>
    /// Takes one sample: <c>offset = serverDate + 0.5 s − midpoint(sentAt, receivedAt)</c>. A sample with a round trip
    /// over <see cref="MaxRoundTrip"/> (or a negative one: the device clock moved meanwhile) or an offset beyond
    /// ±<see cref="MaxOffset"/> is ignored; otherwise it becomes the new anchor and is persisted once.
    /// </summary>
    /// <param name="sentAt">Device UTC time before the request was sent.</param>
    /// <param name="receivedAt">Device UTC time after the response arrived.</param>
    public void Observe(DateTimeOffset serverDate, DateTimeOffset sentAt, DateTimeOffset receivedAt)
    {
        TimeSpan roundTrip = receivedAt - sentAt;
        if (roundTrip < TimeSpan.Zero || roundTrip > MaxRoundTrip)
        {
            return;
        }

        DateTimeOffset midpoint = sentAt + (roundTrip / 2);
        TimeSpan offset = serverDate + DateResolutionHalf - midpoint;
        if (offset.Duration() > MaxOffset)
        {
            return;
        }

        DateTimeOffset deviceNow = Anchor(offset);
        QueuePersist(offset, deviceNow);
    }

    /// <summary>
    /// Anchors on the last persisted offset when it is at most <see cref="MaxPersistedAge"/> old; an absent, unreadable,
    /// stale or future-dated value leaves the device clock in charge until the first response.
    /// </summary>
    public async ValueTask LoadAsync()
    {
        StoredDocument document = StoredDocument.Parse(await store.ReadAsync(StorageKeys.ServerClock));
        if (document.Kind != StoredKind.Valid
            || !document.Root.TryGetProperty(OffsetMember, out JsonElement offsetMember)
            || offsetMember.ValueKind != JsonValueKind.Number
            || !offsetMember.TryGetInt64(out long milliseconds)
            // Not Math.Abs: long.MinValue has no positive counterpart and would throw.
            || milliseconds < -(long)MaxOffset.TotalMilliseconds
            || milliseconds > (long)MaxOffset.TotalMilliseconds
            || !document.Root.TryGetProperty(ObservedAtMember, out JsonElement observedMember)
            || observedMember.ValueKind != JsonValueKind.String
            || !observedMember.TryGetDateTimeOffset(out DateTimeOffset observedAt))
        {
            return;
        }

        TimeSpan age = device.GetUtcNow() - observedAt;
        if (age < TimeSpan.Zero || age > MaxPersistedAge)
        {
            return;
        }

        Anchor(TimeSpan.FromMilliseconds(milliseconds));
    }

    private DateTimeOffset Anchor(TimeSpan offset)
    {
        // The wall clock and the timestamp are read back to back: the only moment the wall clock matters.
        _anchorTimestamp = device.GetTimestamp();
        DateTimeOffset deviceNow = device.GetUtcNow();
        _anchorServerTime = deviceNow + offset;
        return deviceNow;
    }

    private void QueuePersist(TimeSpan offset, DateTimeOffset observedAt)
    {
        lock (_persistLock)
        {
            _pendingWrite = (offset, observedAt);
            if (_writing)
            {
                return;
            }

            _writing = true;
        }

        _ = DrainPersistAsync();
    }

    private async Task DrainPersistAsync()
    {
        try
        {
            while (true)
            {
                (TimeSpan Offset, DateTimeOffset ObservedAt) next;
                lock (_persistLock)
                {
                    if (_pendingWrite is not { } pending)
                    {
                        _writing = false;
                        return;
                    }

                    next = pending;
                    _pendingWrite = null;
                }

                await PersistAsync(next.Offset, next.ObservedAt);
            }
        }
        catch
        {
            lock (_persistLock)
            {
                _writing = false;
            }

            throw;
        }
    }

    private async Task PersistAsync(TimeSpan offset, DateTimeOffset observedAt)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", DocumentVersion);
            writer.WriteNumber(OffsetMember, (long)offset.TotalMilliseconds);
            writer.WriteString(ObservedAtMember, observedAt);
            writer.WriteEndObject();
        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);

        // The offset is a preference: a failed write only means the next page starts without it.
        await store.MutateAsync(StoreOp.Put(StorageKeys.ServerClock, document.RootElement.Clone()));
    }
}
