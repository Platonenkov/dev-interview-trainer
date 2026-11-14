namespace Trainer.Web.Api;

/// <summary>
/// The <c>App:RateLimits</c> section: requests per fixed one-minute window. Generous for a person, low enough to stop a
/// scripted flood. All values must be positive (<see cref="ApiRateLimits.Read"/>).
/// </summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "App:RateLimits";

    /// <summary>Writes per user (<see cref="ApiRateLimits.Writes"/>).</summary>
    public int WritesPerMinute { get; set; } = 60;

    /// <summary>Session and profile reads per user, else per client address (<see cref="ApiRateLimits.Reads"/>).</summary>
    public int ReadsPerMinute { get; set; } = 30;

    /// <summary>
    /// Catalog requests with a search term, per client address (<see cref="Catalog.CatalogSearchRateLimit"/>). They are
    /// the only public requests that are not output-cached; every other public page is never limited.
    /// </summary>
    public int SearchPerMinute { get; set; } = 30;
}
