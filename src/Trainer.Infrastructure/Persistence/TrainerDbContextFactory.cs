using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Trainer.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> build the context without the web host. The connection string only selects the provider
/// while generating migrations; <c>dotnet ef database update</c> reads <c>TRAINER_DB</c> when it is set.
/// </summary>
internal sealed class TrainerDbContextFactory : IDesignTimeDbContextFactory<TrainerDbContext>
{
    private const string DefaultConnectionString = "Host=localhost;Database=dit_design;Username=postgres";

    public TrainerDbContext CreateDbContext(string[] args)
    {
        string connectionString = Environment.GetEnvironmentVariable("TRAINER_DB") ?? DefaultConnectionString;
        DbContextOptionsBuilder<TrainerDbContext> builder = new();
        TrainerDbContextOptions.Configure(builder, connectionString);
        return new TrainerDbContext(builder.Options);
    }
}
