using Microsoft.Extensions.Options;

namespace Trainer.Web.Analytics;

/// <summary>The <c>App:Analytics</c> configuration section (ADR 0018).</summary>
public sealed class AnalyticsOptions
{
    public const string SectionName = "App:Analytics";

    /// <summary>The Umami website id, a GUID. Empty means no tracking script is rendered anywhere.</summary>
    public string? WebsiteId { get; init; }

    /// <summary>The normalised website id, or null when analytics is not configured.</summary>
    public string? GetNormalizedWebsiteId() => Guid.TryParse(WebsiteId, out Guid id) ? id.ToString("D") : null;
}

/// <summary>Reads <see cref="AnalyticsOptions"/> at startup, failing startup on a malformed website id.</summary>
public static class AnalyticsSetup
{
    /// <exception cref="InvalidOperationException">The website id is set but is not a GUID.</exception>
    public static AnalyticsOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        AnalyticsOptions options = configuration.GetSection(AnalyticsOptions.SectionName).Get<AnalyticsOptions>() ?? new AnalyticsOptions();
        if (!string.IsNullOrWhiteSpace(options.WebsiteId) && !Guid.TryParse(options.WebsiteId, out _))
        {
            // The value itself is not echoed: a mistyped id is not a secret, but there is no reason to log configuration.
            throw new InvalidOperationException($"{AnalyticsOptions.SectionName}:{nameof(AnalyticsOptions.WebsiteId)} must be a GUID.");
        }

        return options;
    }

    public static IServiceCollection AddTrainerAnalytics(this IServiceCollection services, AnalyticsOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(Options.Create(options));
        return services;
    }
}
