using Microsoft.Extensions.Options;
using Trainer.Application.Feedback;

namespace Trainer.Web.Startup;

/// <summary>Reads <see cref="FeedbackOptions"/> at startup, the same way as the other options that fail startup when misconfigured.</summary>
public static class FeedbackSetup
{
    /// <exception cref="InvalidOperationException">The daily report limit is not a positive integer.</exception>
    public static FeedbackOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        FeedbackOptions options = configuration.GetSection(FeedbackOptions.SectionName).Get<FeedbackOptions>() ?? new FeedbackOptions();
        if (options.ReportsPerDay <= 0)
        {
            throw new InvalidOperationException(
                $"{FeedbackOptions.SectionName}:{nameof(FeedbackOptions.ReportsPerDay)} must be a positive integer, but was {options.ReportsPerDay}.");
        }

        return options;
    }

    public static IServiceCollection AddTrainerFeedback(this IServiceCollection services, FeedbackOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(Options.Create(options));
        return services;
    }
}
