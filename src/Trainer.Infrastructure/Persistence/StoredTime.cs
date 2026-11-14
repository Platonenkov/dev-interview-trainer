namespace Trainer.Infrastructure.Persistence;

/// <summary>Brings a client-supplied time to the form PostgreSQL stores, so comparisons with read-back values are exact.</summary>
internal static class StoredTime
{
    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    /// <summary>
    /// Converts to UTC (Npgsql writes <c>timestamptz</c> only from offset zero) and truncates to whole microseconds,
    /// the resolution of <c>timestamptz</c>.
    /// </summary>
    public static DateTimeOffset Normalize(DateTimeOffset value)
    {
        DateTimeOffset utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.UtcTicks - (utc.UtcTicks % TicksPerMicrosecond), TimeSpan.Zero);
    }
}
