using Microsoft.EntityFrameworkCore;

namespace Trainer.Infrastructure.Persistence;

internal static class TrainerDbContextOptions
{
    public static void Configure(DbContextOptionsBuilder builder, string connectionString)
    {
        builder
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("ef_migrations_history"))
            .UseSnakeCaseNamingConvention();
    }
}
