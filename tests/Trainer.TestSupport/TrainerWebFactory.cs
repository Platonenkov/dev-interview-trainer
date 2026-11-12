using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Trainer.Web.Startup;

namespace Trainer.TestSupport;

/// <summary>Knobs for <see cref="TrainerWebFactory"/>; the defaults give a normal, fully imported site.</summary>
public sealed class TrainerWebFactoryOptions
{
    /// <summary>
    /// Not Development, so the factory's settings are not overridden by <c>appsettings.Development.json</c>.
    /// </summary>
    public string Environment { get; init; } = "Testing";

    /// <summary>Reuse an existing database (for example one filled by an earlier factory) instead of creating a fresh one.</summary>
    public string? ConnectionString { get; init; }

    /// <summary>Content to serve; defaults to a private copy of the web fixture. The factory deletes the copy it creates.</summary>
    public ContentWorkspace? Content { get; init; }

    public Action<IServiceCollection>? ConfigureServices { get; init; }

    public IReadOnlyDictionary<string, string?>? Settings { get; init; }
}

/// <summary>
/// The web host over a fresh PostgreSQL database and a private copy of the content, shared by the web and E2E tests.
/// The content import runs in the background like in production: call <see cref="WaitUntilReadyAsync"/> before requests.
/// </summary>
public sealed class TrainerWebFactory : WebApplicationFactory<Program>
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);

    private readonly TrainerWebFactoryOptions _options;
    private readonly string _connectionString;
    private readonly bool _ownsContent;

    private TrainerWebFactory(TrainerWebFactoryOptions options, string connectionString)
    {
        _options = options;
        _connectionString = connectionString;
        _ownsContent = options.Content is null;
        Content = options.Content ?? ContentWorkspace.CopyOfWeb();
    }

    /// <summary>The content root the host imports from; tests that mutate content get their own factory.</summary>
    public ContentWorkspace Content { get; }

    public string ConnectionString => _connectionString;

    public static async Task<TrainerWebFactory> CreateAsync(PostgresFixture postgres, TrainerWebFactoryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(postgres);
        TrainerWebFactoryOptions effective = options ?? new TrainerWebFactoryOptions();
        string connectionString = effective.ConnectionString ?? await postgres.CreateDatabaseAsync();
        return new TrainerWebFactory(effective, connectionString);
    }

    /// <summary>A client that does not follow redirects, for asserting on <c>Location</c>.</summary>
    public HttpClient CreateNonRedirectingClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>Polls <c>/health/ready</c> until it is 200. Fails fast when the startup import has already failed.</summary>
    public async Task WaitUntilReadyAsync()
    {
        using HttpClient client = CreateClient();
        ReadinessState state = Services.GetRequiredService<ReadinessState>();
        using CancellationTokenSource timeout = new(ReadyTimeout);
        HttpStatusCode last = default;
        try
        {
            while (true)
            {
                using HttpResponseMessage response = await client.GetAsync("/health/ready", timeout.Token);
                last = response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                if (state.Status == ReadinessStatus.Failed)
                {
                    throw new InvalidOperationException("The startup content import failed; see the host log.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"/health/ready was not 200 within {ReadyTimeout.TotalSeconds:0} s (last status {(int)last}).");
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_options.Environment);
        // Outside Development the host ignores the static web assets manifest, and a browser that accepts gzip then gets empty compressed assets.
        builder.UseStaticWebAssets();
        builder.ConfigureLogging(logging => logging.ClearProviders());
        // UseSetting rather than ConfigureAppConfiguration: Program reads these keys before the host is built.
        builder.UseSetting("ConnectionStrings:Trainer", _connectionString);
        builder.UseSetting("App:ContentRoot", Content.Root);
        builder.UseSetting("App:PublicBaseUrl", "https://trainer.test");
        if (_options.Settings is not null)
        {
            foreach (KeyValuePair<string, string?> setting in _options.Settings)
            {
                builder.UseSetting(setting.Key, setting.Value);
            }
        }

        if (_options.ConfigureServices is not null)
        {
            builder.ConfigureServices(_options.ConfigureServices);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && _ownsContent)
        {
            Content.Dispose();
        }
    }
}
