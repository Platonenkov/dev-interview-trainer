using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Trainer.Web.Startup;

namespace Trainer.TestSupport;

/// <summary>Knobs for <see cref="TrainerWebFactory"/>; the defaults give a normal, fully imported site.</summary>
public sealed class TrainerWebFactoryOptions
{
    /// <summary>
    /// Not Development, so the factory's settings are not overridden by <c>appsettings.Development.json</c>. The
    /// user-secrets source is removed from every host this factory builds, so a Development host never reads a
    /// developer's real <c>TelegramOidc:ClientSecret</c>.
    /// </summary>
    public string Environment { get; init; } = "Testing";

    /// <summary>Reuse an existing database (for example one filled by an earlier factory) instead of creating a fresh one.</summary>
    public string? ConnectionString { get; init; }

    /// <summary>Content to serve; defaults to a private copy of the web fixture. The factory deletes the copy it creates.</summary>
    public ContentWorkspace? Content { get; init; }

    public Action<IServiceCollection>? ConfigureServices { get; init; }

    public IReadOnlyDictionary<string, string?>? Settings { get; init; }

    /// <summary>
    /// Points the app's Telegram OIDC handler at this fake server: sets <c>TelegramOidc:ClientId</c> and
    /// <c>TelegramOidc:ClientSecret</c> to the server's own, and <c>TelegramOidc:MetadataAddress</c>.
    /// The app honours the metadata address only in the Testing environment.
    /// </summary>
    public FakeTelegramOidcServer? Telegram { get; init; }

    /// <summary>Replaces the app's <see cref="TimeProvider"/>, for example with a <c>FakeTimeProvider</c>.</summary>
    public TimeProvider? Time { get; init; }

    /// <summary>Receives every log entry the host writes; without it the host logs nowhere.</summary>
    public CapturingLoggerProvider? Logs { get; init; }

    /// <summary>The client id tests start a <see cref="FakeTelegramOidcServer"/> with; the factory reads the server's own <c>ClientId</c>.</summary>
    public const string TelegramClientId = "1000";
}

/// <summary>
/// The web host over a fresh PostgreSQL database and a private copy of the content, shared by the web and E2E tests.
/// The content import runs in the background like in production: call <see cref="WaitUntilReadyAsync"/> before requests.
/// </summary>
public sealed class TrainerWebFactory : WebApplicationFactory<Program>
{
    private static readonly Uri DefaultClientAddress = new("https://localhost");
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The per-minute <c>App:RateLimits</c> every test host runs with unless <see cref="TrainerWebFactoryOptions.Settings"/> overrides it.</summary>
    public const string RaisedRateLimit = "100000";

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
        base.CreateClient(DefaultClientOptions(allowAutoRedirect: false));

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

    /// <summary>
    /// In-memory clients talk https, like production behind the proxy, so Secure cookies round-trip. This hides the base
    /// method rather than overriding it (it is not virtual): code that holds the host as a plain
    /// <c>WebApplicationFactory&lt;Program&gt;</c> gets the http default. Only the client
    /// default changes: <c>ClientOptions</c> stays as it is, because <c>UseKestrel</c> stores its real address there,
    /// and a caller that passes its own options to <c>CreateClient(options)</c> still gets exactly that address.
    /// </summary>
    public new HttpClient CreateClient() => base.CreateClient(DefaultClientOptions(allowAutoRedirect: ClientOptions.AllowAutoRedirect));

    private WebApplicationFactoryClientOptions DefaultClientOptions(bool allowAutoRedirect) => new()
    {
        AllowAutoRedirect = allowAutoRedirect,
        HandleCookies = ClientOptions.HandleCookies,
        MaxAutomaticRedirections = ClientOptions.MaxAutomaticRedirections,
        BaseAddress = IsDefaultClientAddress(ClientOptions.BaseAddress) ? DefaultClientAddress : ClientOptions.BaseAddress,
    };

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        if (IsDefaultClientAddress(ClientOptions.BaseAddress))
        {
            client.BaseAddress = DefaultClientAddress;
        }
    }

    private static bool IsDefaultClientAddress(Uri address) =>
        address.IsLoopback && address.Host == "localhost" && address.IsDefaultPort;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_options.Environment);
        // Outside Development the host ignores the static web assets manifest, and a browser that accepts gzip then gets empty compressed assets.
        builder.UseStaticWebAssets();
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            if (_options.Logs is not null)
            {
                logging.AddProvider(_options.Logs);
            }
        });
        // A Development host would otherwise load the developer's user secrets (a real Telegram client secret among
        // them); a test host never depends on them, so the source is removed.
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            for (int i = configuration.Sources.Count - 1; i >= 0; i--)
            {
                if (configuration.Sources[i] is Microsoft.Extensions.Configuration.Json.JsonConfigurationSource { Path: "secrets.json" })
                {
                    configuration.Sources.RemoveAt(i);
                }
            }
        });
        // UseSetting rather than ConfigureAppConfiguration: Program reads these keys before the host is built.
        builder.UseSetting("ConnectionStrings:Trainer", _connectionString);
        builder.UseSetting("App:ContentRoot", Content.Root);
        builder.UseSetting("App:PublicBaseUrl", "https://trainer.test");
        // Tests fire requests far faster than a person; only the dedicated rate limit tests lower these through Settings.
        builder.UseSetting("App:RateLimits:WritesPerMinute", RaisedRateLimit);
        builder.UseSetting("App:RateLimits:ReadsPerMinute", RaisedRateLimit);
        builder.UseSetting("App:RateLimits:SearchPerMinute", RaisedRateLimit);

        // Production refuses to start without Telegram credentials; Production tests that never sign in get inert ones,
        // which Settings may still override.
        if (_options.Telegram is null && string.Equals(_options.Environment, "Production", StringComparison.Ordinal))
        {
            builder.UseSetting("TelegramOidc:ClientId", TrainerWebFactoryOptions.TelegramClientId);
            builder.UseSetting("TelegramOidc:ClientSecret", "production-test-placeholder");
        }

        if (_options.Settings is not null)
        {
            foreach (KeyValuePair<string, string?> setting in _options.Settings)
            {
                builder.UseSetting(setting.Key, setting.Value);
            }
        }

        if (_options.Telegram is not null)
        {
            builder.UseSetting("TelegramOidc:ClientId", _options.Telegram.ClientId);
            builder.UseSetting("TelegramOidc:ClientSecret", _options.Telegram.ClientSecret);
            builder.UseSetting("TelegramOidc:MetadataAddress", _options.Telegram.MetadataAddress.ToString());
        }

        if (_options.Time is not null)
        {
            builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton(_options.Time)));
        }

        if (_options.ConfigureServices is not null)
        {
            builder.ConfigureServices(_options.ConfigureServices);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        // A stopped host leaves its pooled connections idle for minutes; across a long run they exhaust the
        // server's connection limit and later hosts never finish their startup import.
        using NpgsqlConnection pooled = new(_connectionString);
        NpgsqlConnection.ClearPool(pooled);
        if (_ownsContent)
        {
            Content.Dispose();
        }
    }
}
