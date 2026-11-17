using System.Diagnostics;
using System.Reflection;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Trainer.Infrastructure.Persistence;

namespace Trainer.Infrastructure.Tests;

/// <summary>
/// Runs the migration bundle shipped in the production image against a real database through the TRAINER_DB path, as the
/// deploy does. Needs the built image: set <c>DIT_MIGRATE_IMAGE</c> (for example <c>dit:local</c>); skipped otherwise.
/// </summary>
[Trait("Category", "Docker")]
public sealed class MigrationBundleTests
{
    private const string ImageVariable = "DIT_MIGRATE_IMAGE";

    [Fact]
    public async Task Bundle_AppliesEveryMigrationAndIsIdempotent()
    {
        string? image = Environment.GetEnvironmentVariable(ImageVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(image), $"{ImageVariable} is not set; build the image and set it to run this test.");

        await using INetwork network = new NetworkBuilder().Build();
        await network.CreateAsync(TestContext.Current.CancellationToken);
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine")
            .WithNetwork(network)
            .WithNetworkAliases("pg")
            .Build();
        await postgres.StartAsync(TestContext.Current.CancellationToken);

        string trainerDb = "Host=pg;Port=5432;Database=postgres;Username=postgres;Password=postgres";
        (int first, string firstOutput) = await RunBundleAsync(image!, network.Name, trainerDb);
        (int second, string secondOutput) = await RunBundleAsync(image!, network.Name, trainerDb);

        Assert.True(first == 0, firstOutput);
        Assert.True(second == 0, secondOutput);

        List<string> expected = typeof(TrainerDbContext).Assembly.GetTypes()
            .Select(t => t.GetCustomAttribute<MigrationAttribute>()?.Id)
            .OfType<string>()
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(expected);

        List<string> applied = [];
        await using NpgsqlConnection connection = new(postgres.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """SELECT migration_id FROM ef_migrations_history ORDER BY migration_id""";
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                applied.Add(reader.GetString(0));
            }
        }

        Assert.Equal(expected, applied);
    }

    private static async Task<(int ExitCode, string Output)> RunBundleAsync(string image, string network, string trainerDb)
    {
        ProcessStartInfo startInfo = new("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in new[] { "run", "--rm", "--network", network, "-e", $"TRAINER_DB={trainerDb}", "--entrypoint", "/app/migrate/efbundle", image })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await stdout + await stderr);
    }
}
