using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Trainer.Application.Content;
using Trainer.TestSupport;
using Trainer.Web.Startup;

namespace Trainer.Web.Tests;

public sealed class ReadinessTests(PostgresFixture postgres)
{
    private sealed class FakeImporter(Func<CancellationToken, Task<ContentImportResult>> import) : IContentImportService
    {
        public Task<ContentImportResult> ImportAsync(string contentRoot, CancellationToken ct) => import(ct);
    }

    private static ContentImportResult Result(ContentImportStatus status, params ImportIssue[] issues) =>
        new(status, "test-hash", 0, 0, issues);

    private static Action<IServiceCollection> UseImporter(Func<CancellationToken, Task<ContentImportResult>> import) =>
        services =>
        {
            services.RemoveAll<IContentImportService>();
            services.AddScoped<IContentImportService>(_ => new FakeImporter(import));
        };

    private static async Task WaitForStatusAsync(TrainerWebFactory factory, ReadinessStatus expected)
    {
        ReadinessState state = factory.Services.GetRequiredService<ReadinessState>();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        while (state.Status != expected)
        {
            await Task.Delay(50, timeout.Token);
        }
    }

    [Fact]
    public async Task Readiness_PendingThenReady()
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(
            postgres,
            new TrainerWebFactoryOptions
            {
                ConfigureServices = UseImporter(async ct =>
                {
                    await gate.Task.WaitAsync(ct);
                    return Result(ContentImportStatus.Imported);
                }),
            });
        using HttpClient client = factory.CreateClient();

        using (HttpResponseMessage pending = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, pending.StatusCode);
        }

        using (HttpResponseMessage live = await client.GetAsync("/health/live", TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        }

        gate.SetResult();
        await factory.WaitUntilReadyAsync();

        using HttpResponseMessage ready = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task Readiness_RealImport_BecomesReadyAndServesStack()
    {
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres);
        await factory.WaitUntilReadyAsync();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/ru/dotnet", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public static TheoryData<bool> FailureModes() => [true, false];

    [Theory]
    [MemberData(nameof(FailureModes))]
    public async Task Readiness_FailedImport_StaysUnready_AndPagesStillServe(bool throws)
    {
        string connectionString = await postgres.CreateDatabaseAsync();
        await using (TrainerWebFactory seed = await TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions { ConnectionString = connectionString }))
        {
            await seed.WaitUntilReadyAsync();
        }

        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(
            postgres,
            new TrainerWebFactoryOptions
            {
                ConnectionString = connectionString,
                ConfigureServices = UseImporter(_ => throws
                    ? Task.FromException<ContentImportResult>(new IOException("disk gone"))
                    : Task.FromResult(Result(ContentImportStatus.Rejected, new ImportIssue("a.yaml", "X001", "bad")))),
            });
        using HttpClient client = factory.CreateClient();
        await WaitForStatusAsync(factory, ReadinessStatus.Failed);

        using HttpResponseMessage ready = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        using HttpResponseMessage live = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        using HttpResponseMessage page = await client.GetAsync("/ru/dotnet", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }

    [Fact]
    public async Task StartupMigrationFlag_OutsideDevelopment_Throws()
    {
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(
            postgres,
            new TrainerWebFactoryOptions
            {
                Environment = "Production",
                Settings = new Dictionary<string, string?> { ["App:ApplyMigrationsOnStartup"] = "true" },
            });

        Exception thrown = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("ApplyMigrationsOnStartup", Flatten(thrown), StringComparison.Ordinal);
    }

    private static string Flatten(Exception exception)
    {
        List<string> messages = [];
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }
}
