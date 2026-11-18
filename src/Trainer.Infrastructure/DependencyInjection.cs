using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Trainer.Application.Admin;
using Trainer.Application.Answers;
using Trainer.Application.Catalog;
using Trainer.Application.Content;
using Trainer.Application.Feedback;
using Trainer.Application.Home;
using Trainer.Application.Mock;
using Trainer.Application.Progress;
using Trainer.Application.Questions;
using Trainer.Application.Seo;
using Trainer.Application.Sessions;
using Trainer.Application.StaticSite;
using Trainer.Application.Stacks;
using Trainer.Application.Topics;
using Trainer.Application.Users;
using Trainer.Content.Reading;
using Trainer.Content.Rendering;
using Trainer.Content.Validation;
using Trainer.Domain.Ids;
using Trainer.Domain.Scheduling;
using Trainer.Domain.Sessions;
using Trainer.Infrastructure.Admin;
using Trainer.Infrastructure.Answers;
using Trainer.Infrastructure.Content;
using Trainer.Infrastructure.Feedback;
using Trainer.Infrastructure.Mock;
using Trainer.Infrastructure.Persistence;
using Trainer.Infrastructure.Progress;
using Trainer.Infrastructure.Queries;
using Trainer.Infrastructure.Sessions;
using Trainer.Infrastructure.StaticSite;
using Trainer.Infrastructure.Users;

namespace Trainer.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the pooled <see cref="TrainerDbContext"/>, the content services, the import service, the query
    /// services, user accounts, answer recording, mock sessions, the session queries, the progress queries and the admin queries.
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
        services.AddOptions();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<UlidGenerator>();
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
        services.TryAddScoped<IContentStatusQueries, ContentStatusQueries>();
        services.TryAddSingleton<IContentSchemaInfo, ContentSchemaInfo>();
        services.TryAddScoped<IUserAccountService, UserAccountService>();
        services.TryAddSingleton<IReviewScheduler, Sm2ReviewScheduler>();
        services.TryAddScoped<IAnswerRecorder, AnswerRecorder>();
        services.TryAddScoped<IMockSessionService, MockSessionService>();
        services.TryAddSingleton(new MockComposer(Random.Shared));
        services.TryAddScoped<ISessionQueries, SessionQueries>();
        services.TryAddScoped<IStaticSiteQueries, StaticSiteQueries>();
        services.TryAddScoped<IProgressQueries, ProgressQueries>();
        services.TryAddScoped<IAdminQueries, AdminQueries>();
        services.TryAddScoped<IQuestionFeedbackService, QuestionFeedbackService>();
        services.TryAddScoped<IQuestionFeedbackQueries, QuestionFeedbackQueries>();
        return services;
    }
}
