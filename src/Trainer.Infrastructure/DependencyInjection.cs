using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Trainer.Application.Catalog;
using Trainer.Application.Content;
using Trainer.Application.Home;
using Trainer.Application.Questions;
using Trainer.Application.Seo;
using Trainer.Application.Stacks;
using Trainer.Application.Topics;
using Trainer.Content.Reading;
using Trainer.Content.Rendering;
using Trainer.Content.Validation;
using Trainer.Infrastructure.Content;
using Trainer.Infrastructure.Persistence;
using Trainer.Infrastructure.Queries;

namespace Trainer.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the pooled <see cref="TrainerDbContext"/>, the content services and the import service.
    /// <para>
    /// <c>IContentCacheInvalidator</c> is deliberately not registered here: each host supplies its own
    /// implementation (the web app drops its output cache, tests use fakes), and resolving the import service
    /// fails until one is registered.
    /// </para>
    /// </summary>
    public static IServiceCollection AddTrainerInfrastructure(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContextPool<TrainerDbContext>(options => TrainerDbContextOptions.Configure(options, connectionString));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IContentReader, ContentReader>();
        services.TryAddSingleton<IContentRenderer, ContentRenderer>();
        services.TryAddSingleton<IContentValidator, ContentValidator>();
        services.TryAddScoped<IContentImportService, ContentImportService>();
        services.TryAddScoped<IStackQueries, StackQueries>();
        services.TryAddScoped<ICatalogQueries, CatalogQueries>();
        services.TryAddScoped<IHomeQueries, HomeQueries>();
        services.TryAddScoped<IQuestionQueries, QuestionQueries>();
        services.TryAddScoped<ITopicQueries, TopicQueries>();
        services.TryAddScoped<ISitemapQueries, SitemapQueries>();
        return services;
    }
}
