using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Trainer.Application.Content;
using Trainer.Application.Stacks;
using Trainer.Domain.Keys;
using Trainer.TestSupport;
using Trainer.Web.Startup;

namespace Trainer.Web.Tests;

public sealed class StartupImportTests(PostgresFixture postgres)
{
    private sealed class FakeImporter(Func<int, Task<ContentImportResult>> import) : IContentImportService
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<ContentImportResult> ImportAsync(string contentRoot, CancellationToken ct) => import(Interlocked.Increment(ref _calls));
    }

    /// <summary>Fires every timer at once and remembers how long it was asked to wait, so backoff tests run instantly.</summary>
    private sealed class ImmediateTimeProvider : TimeProvider
    {
        private readonly List<TimeSpan> _delays = [];

        public IReadOnlyList<TimeSpan> Delays
        {
            get
            {
                lock (_delays)
                {
                    return [.. _delays];
                }
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_delays)
            {
                _delays.Add(dueTime);
            }

            ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new NoopTimer();
        }

        private sealed class NoopTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static ContentImportResult Imported() => new(ContentImportStatus.Imported, "test-hash", 0, 0, []);

    private static NpgsqlException Transient() => new("connection refused", new SocketException());

    private static Action<IServiceCollection> UseImporter(FakeImporter importer, TimeProvider? time = null) =>
        services =>
        {
            services.RemoveAll<IContentImportService>();
            services.AddScoped<IContentImportService>(_ => importer);
            if (time is not null)
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(time);
            }
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

    private static Task<TrainerWebFactory> CreateAsync(
        PostgresFixture postgres, FakeImporter importer, ImmediateTimeProvider time, IReadOnlyDictionary<string, string?>? settings = null) =>
        TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions { ConfigureServices = UseImporter(importer, time), Settings = settings });

    [Fact]
    public async Task TransientFailureTwice_ThenSuccess_BecomesReady_WithExponentialBackoff()
    {
        FakeImporter importer = new(call => call < 3 ? Task.FromException<ContentImportResult>(Transient()) : Task.FromResult(Imported()));
        ImmediateTimeProvider time = new();
        await using TrainerWebFactory factory = await CreateAsync(postgres, importer, time);

        await WaitForStatusAsync(factory, ReadinessStatus.Ready);

        Assert.Equal(3, importer.Calls);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], time.Delays.Where(d => d >= TimeSpan.FromSeconds(1)).ToArray());
    }

    [Fact]
    public async Task TransientFailureWrappedByEfCore_IsRetried()
    {
        FakeImporter importer = new(call => call < 2
            ? Task.FromException<ContentImportResult>(new InvalidOperationException("wrapper", new IOException("io", Transient())))
            : Task.FromResult(Imported()));
        await using TrainerWebFactory factory = await CreateAsync(postgres, importer, new ImmediateTimeProvider());

        await WaitForStatusAsync(factory, ReadinessStatus.Ready);

        Assert.Equal(2, importer.Calls);
    }

    [Fact]
    public async Task NonTransientFailure_IsNotRetried()
    {
        FakeImporter importer = new(_ => Task.FromException<ContentImportResult>(new IOException("disk gone")));
        await using TrainerWebFactory factory = await CreateAsync(postgres, importer, new ImmediateTimeProvider());

        await WaitForStatusAsync(factory, ReadinessStatus.Failed);

        Assert.Equal(1, importer.Calls);
    }

    [Fact]
    public async Task TransientFailureBeyondMaxAttempts_StaysFailed_KeepsRetryingAtMaxDelay_AndRecovers()
    {
        int allowSuccess = 0;
        FakeImporter importer = new(_ => Volatile.Read(ref allowSuccess) == 1
            ? Task.FromResult(Imported())
            : Task.FromException<ContentImportResult>(Transient()));
        ImmediateTimeProvider time = new();
        await using TrainerWebFactory factory = await CreateAsync(
            postgres,
            importer,
            time,
            new Dictionary<string, string?> { ["App:StartupImport:MaxAttempts"] = "3" });

        await WaitForStatusAsync(factory, ReadinessStatus.Failed);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        while (importer.Calls < 6)
        {
            await Task.Delay(20, timeout.Token);
        }

        Assert.Equal(ReadinessStatus.Failed, factory.Services.GetRequiredService<ReadinessState>().Status);
        TimeSpan[] delays = [.. time.Delays.Where(d => d >= TimeSpan.FromSeconds(1))];
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delays.Take(2).ToArray());
        Assert.All(delays.Skip(2), d => Assert.Equal(TimeSpan.FromSeconds(30), d));

        Volatile.Write(ref allowSuccess, 1);
        await WaitForStatusAsync(factory, ReadinessStatus.Ready);
    }

    [Fact]
    public async Task ShutdownDuringBackoff_StopsTheImporter()
    {
        FakeImporter importer = new(_ => Task.FromException<ContentImportResult>(Transient()));
        TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(
            postgres,
            new TrainerWebFactoryOptions
            {
                ConfigureServices = UseImporter(importer),
                Settings = new Dictionary<string, string?> { ["App:StartupImport:InitialDelay"] = "01:00:00", ["App:StartupImport:MaxDelay"] = "01:00:00" },
            });
        using HttpClient client = factory.CreateClient();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        while (importer.Calls == 0)
        {
            await Task.Delay(50, timeout.Token);
        }

        await factory.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        Assert.Equal(1, importer.Calls);
    }

    [Fact]
    public async Task NotReady_ThenLaterSuccessfulImport_BecomesReady()
    {
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres);
        await factory.WaitUntilReadyAsync();
        ReadinessState state = factory.Services.GetRequiredService<ReadinessState>();
        state.MarkFailed();
        Assert.Equal(ReadinessStatus.Failed, state.Status);

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            IContentImportService import = scope.ServiceProvider.GetRequiredService<IContentImportService>();
            ContentImportResult result = await import.ImportAsync(factory.Content.Root, TestContext.Current.CancellationToken);
            Assert.Equal(ContentImportStatus.Unchanged, result.Status);
        }

        Assert.Equal(ReadinessStatus.Ready, state.Status);
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage ready = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task RejectedStartupImport_ThenFixedContent_BecomesReady()
    {
        using ContentWorkspace content = ContentWorkspace.CopyOfWeb();
        content.Write("dotnet/csharp/broken.yaml", "not: [valid");
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions { Content = content });
        await WaitForStatusAsync(factory, ReadinessStatus.Failed);

        content.Delete("dotnet/csharp/broken.yaml");
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            IContentImportService import = scope.ServiceProvider.GetRequiredService<IContentImportService>();
            ContentImportResult result = await import.ImportAsync(content.Root, TestContext.Current.CancellationToken);
            Assert.Equal(ContentImportStatus.Imported, result.Status);
        }

        Assert.Equal(ReadinessStatus.Ready, factory.Services.GetRequiredService<ReadinessState>().Status);
    }

    [Fact]
    public async Task UnchangedRestart_IsNotReadyUntilTheSnapshotRefreshSucceeds()
    {
        string connectionString = await postgres.CreateDatabaseAsync();
        using ContentWorkspace content = ContentWorkspace.CopyOfWeb();
        await using (TrainerWebFactory seed = await TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions { ConnectionString = connectionString, Content = content }))
        {
            await seed.WaitUntilReadyAsync();
        }

        GatedStackQueries gate = new();
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(
            postgres,
            new TrainerWebFactoryOptions
            {
                ConnectionString = connectionString,
                Content = content,
                Settings = new Dictionary<string, string?> { ["App:StartupImport:InitialDelay"] = "00:00:00.100" },
                ConfigureServices = services =>
                {
                    ServiceDescriptor original = services.Single(d => d.ServiceType == typeof(IStackQueries));
                    services.Remove(original);
                    services.AddSingleton(gate);
                    services.AddScoped<IStackQueries>(provider => new GatedStackQueries.Wrapper(
                        (IStackQueries)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!),
                        provider.GetRequiredService<GatedStackQueries>()));
                },
            });
        using HttpClient client = factory.CreateClient();
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        ReadinessState state = factory.Services.GetRequiredService<ReadinessState>();
        using (HttpResponseMessage whileRefreshing = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken))
        {
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, whileRefreshing.StatusCode);
        }

        Assert.NotEqual(ReadinessStatus.Ready, state.Status);
        gate.ReleaseWithTransientFailure();
        await factory.WaitUntilReadyAsync();

        Assert.True(factory.Services.GetRequiredService<ContentState.ContentSnapshotProvider>().Current.Generation > 0);
        Assert.Equal(2, gate.Calls);
    }

    /// <summary>Blocks the first taxonomy read, then fails it with a transient error; later reads pass through.</summary>
    private sealed class GatedStackQueries
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task Entered => _entered.Task;

        public int Calls => Volatile.Read(ref _calls);

        public void ReleaseWithTransientFailure() => _released.TrySetResult();

        public sealed class Wrapper(IStackQueries inner, GatedStackQueries gate) : IStackQueries
        {
            public async Task<IReadOnlyDictionary<StackKey, IReadOnlyList<TopicKey>>> GetTaxonomyAsync(CancellationToken ct)
            {
                if (Interlocked.Increment(ref gate._calls) == 1)
                {
                    gate._entered.TrySetResult();
                    await gate._released.Task.WaitAsync(ct);
                    throw Transient();
                }

                return await inner.GetTaxonomyAsync(ct);
            }
        }
    }

    [Fact]
    public async Task RejectedImport_DoesNotMarkReady()
    {
        FakeImporter inner = new(_ => Task.FromResult(new ContentImportResult(ContentImportStatus.Rejected, "h", 0, 0, [new ImportIssue("a.yaml", "X001", "bad")])));
        ReadinessState state = new();
        state.MarkFailed();
        ServiceCollection services = new();
        using ContentState.ContentSnapshotProvider snapshots = new(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
        ReadinessReportingImportService service = new(inner, state, snapshots, NullLogger<ReadinessReportingImportService>.Instance);

        await service.ImportAsync("unused", TestContext.Current.CancellationToken);

        Assert.Equal(ReadinessStatus.Failed, state.Status);
    }

    private sealed class ThrowingStackQueries : IStackQueries
    {
        public Task<IReadOnlyDictionary<StackKey, IReadOnlyList<TopicKey>>> GetTaxonomyAsync(CancellationToken ct) =>
            throw new InvalidOperationException("database is down");
    }

    private sealed class ErrorCapture : ILogger<ReadinessReportingImportService>
    {
        public int Errors { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error && exception is not null)
            {
                Errors++;
            }
        }
    }

    private static ContentState.ContentSnapshotProvider FailingSnapshots()
    {
        ServiceCollection services = new();
        services.AddSingleton<IStackQueries>(new ThrowingStackQueries());
        return new ContentState.ContentSnapshotProvider(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
    }

    [Fact]
    public async Task ImportedResult_SurvivesASnapshotRefreshFailure_ButIsNotReady()
    {
        FakeImporter inner = new(_ => Task.FromResult(Imported()));
        ReadinessState state = new();
        using ContentState.ContentSnapshotProvider snapshots = FailingSnapshots();
        ErrorCapture logger = new();
        ReadinessReportingImportService service = new(inner, state, snapshots, logger);

        ContentImportResult result = await service.ImportAsync("unused", TestContext.Current.CancellationToken);

        Assert.Equal(ContentImportStatus.Imported, result.Status);
        Assert.NotEqual(ReadinessStatus.Ready, state.Status);
        Assert.Equal(1, logger.Errors);
    }

    [Fact]
    public async Task UnchangedResult_PropagatesASnapshotRefreshFailure()
    {
        FakeImporter inner = new(_ => Task.FromResult(new ContentImportResult(ContentImportStatus.Unchanged, "h", 0, 0, [])));
        using ContentState.ContentSnapshotProvider snapshots = FailingSnapshots();
        ReadinessReportingImportService service = new(inner, new ReadinessState(), snapshots, NullLogger<ReadinessReportingImportService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync("unused", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(40, 30)]
    public void DelayAfter_DoublesAndIsCappedAtMaxDelay(int failedAttempts, int expectedSeconds)
    {
        StartupImportOptions options = new();

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), options.DelayAfter(failedAttempts));
    }

    [Fact]
    public void TransientFailure_RecognisesNetworkAndTimeoutErrorsAnywhereInTheChain()
    {
        Assert.True(TransientFailure.IsTransient(new TimeoutException()));
        Assert.True(TransientFailure.IsTransient(new SocketException()));
        Assert.True(TransientFailure.IsTransient(new InvalidOperationException("outer", Transient())));
        Assert.False(TransientFailure.IsTransient(new InvalidOperationException("outer", new IOException("disk"))));
        Assert.False(TransientFailure.IsTransient(new DirectoryNotFoundException()));
    }
}
