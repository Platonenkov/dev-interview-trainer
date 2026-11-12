using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Trainer.Infrastructure.Persistence;

namespace Trainer.TestSupport;

/// <summary>
/// One PostgreSQL container per test run. Migrations are applied once into a template database; every test
/// then gets a private copy through <see cref="CreateDatabaseAsync"/>, which is much faster than migrating again.
/// Register it as an xUnit assembly fixture.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string TemplateDatabase = "dit_template";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await CreateTemplateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await _container.DisposeAsync();
    }

    /// <summary>Creates a fresh database from the migrated template and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        string name = $"dit_{Guid.NewGuid():N}";
        await using NpgsqlConnection admin = new(_container.GetConnectionString());
        await admin.OpenAsync();
        await using NpgsqlCommand command = admin.CreateCommand();
        // The name is a generated hex identifier, so concatenation cannot inject anything.
        command.CommandText = $"CREATE DATABASE {name} TEMPLATE {TemplateDatabase}";
        await command.ExecuteNonQueryAsync();
        return WithDatabase(name, pooling: true);
    }

    public static TrainerDbContext CreateContext(string connectionString)
    {
        DbContextOptionsBuilder<TrainerDbContext> builder = new();
        TrainerDbContextOptions.Configure(builder, connectionString);
        return new TrainerDbContext(builder.Options);
    }

    private async Task CreateTemplateAsync()
    {
        await using (NpgsqlConnection admin = new(_container.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using NpgsqlCommand create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE {TemplateDatabase}";
            await create.ExecuteNonQueryAsync();
        }

        // PostgreSQL refuses to copy a template that has open connections, so the template is migrated over
        // unpooled connections and nothing of it stays in the pool afterwards.
        await using (TrainerDbContext context = CreateContext(WithDatabase(TemplateDatabase, pooling: false)))
        {
            await context.Database.MigrateAsync();
        }

        NpgsqlConnection.ClearAllPools();
    }

    private string WithDatabase(string database, bool pooling)
    {
        NpgsqlConnectionStringBuilder builder = new(_container.GetConnectionString())
        {
            Database = database,
            Pooling = pooling,
        };
        return builder.ConnectionString;
    }
}
