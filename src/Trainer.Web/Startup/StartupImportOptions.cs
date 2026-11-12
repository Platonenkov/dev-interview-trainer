namespace Trainer.Web.Startup;

/// <summary>The <c>App:StartupImport</c> section: how long the host keeps retrying the first import while the database is unavailable.</summary>
public sealed class StartupImportOptions
{
    /// <summary>Total attempts including the first one; values below 1 mean a single attempt.</summary>
    public int MaxAttempts { get; init; } = 8;

    /// <summary>Delay before the second attempt; it doubles after every failed attempt.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound for the delay between two attempts.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The wait before attempt number <paramref name="failedAttempts"/> + 1.</summary>
    public TimeSpan DelayAfter(int failedAttempts)
    {
        double factor = Math.Pow(2, Math.Max(0, failedAttempts - 1));
        double milliseconds = Math.Min(InitialDelay.TotalMilliseconds * factor, MaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
    }
}
