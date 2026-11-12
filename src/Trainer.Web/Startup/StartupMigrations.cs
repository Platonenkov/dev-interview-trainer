using Microsoft.EntityFrameworkCore;
using Trainer.Infrastructure.Persistence;

namespace Trainer.Web.Startup;

public static class StartupMigrations
{
    /// <summary>Production schema changes are a deliberate deployment step, so the flag outside Development is a configuration error.</summary>
    public static void EnsureAllowed(AppOptions options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        if (options.ApplyMigrationsOnStartup && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"App:ApplyMigrationsOnStartup is only allowed in the Development environment (current: {environment.EnvironmentName}).");
        }
    }

    public static async Task ApplyAsync(IServiceProvider services, CancellationToken ct)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        TrainerDbContext db = scope.ServiceProvider.GetRequiredService<TrainerDbContext>();
        await db.Database.MigrateAsync(ct);
    }
}
