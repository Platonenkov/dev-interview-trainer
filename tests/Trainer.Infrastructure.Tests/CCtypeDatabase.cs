using Microsoft.EntityFrameworkCore;
using Npgsql;
using Trainer.Infrastructure.Persistence;
using Trainer.TestSupport;

namespace Trainer.Infrastructure.Tests;

/// <summary>A migrated database whose ctype is <c>C</c>, where the default <c>lower()</c> and <c>ILIKE</c> only fold ASCII.</summary>
internal static class CCtypeDatabase
{
    public static async Task<string> CreateAsync(PostgresFixture postgres, CancellationToken ct)
    {
        string any = await postgres.CreateDatabaseAsync();
        string name = $"dit_c_{Guid.NewGuid():N}";
        NpgsqlConnectionStringBuilder admin = new(any) { Database = "postgres", Pooling = false };
        await using (NpgsqlConnection connection = new(admin.ConnectionString))
        {
            await connection.OpenAsync(ct);
            await using NpgsqlCommand create = connection.CreateCommand();
            create.CommandText = $"CREATE DATABASE {name} TEMPLATE template0 ENCODING 'UTF8' LC_COLLATE 'C' LC_CTYPE 'C'";
            await create.ExecuteNonQueryAsync(ct);
        }

        NpgsqlConnectionStringBuilder target = new(any) { Database = name, Pooling = false };
        await using (TrainerDbContext migrate = PostgresFixture.CreateContext(target.ConnectionString))
        {
            await migrate.Database.MigrateAsync(ct);
        }

        return target.ConnectionString;
    }
}
