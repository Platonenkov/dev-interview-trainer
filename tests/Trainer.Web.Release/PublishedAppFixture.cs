using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Playwright;
using Npgsql;
using Trainer.TestSupport;

namespace Trainer.Web.Release;

/// <summary>
/// The published Release build (<c>dotnet publish -c Release</c>, named by <c>DIT_RELEASE_PUBLISH_DIR</c>) running as a
/// child process on <c>http://localhost:5080</c> over an already migrated database (<c>DIT_RELEASE_CONNECTION</c>) and
/// the repository <c>content/</c>, signed in through a <see cref="FakeTelegramOidcServer"/>, plus one headless
/// Chromium. Without the two variables nothing starts and every test skips.
/// </summary>
public sealed class PublishedAppFixture : IAsyncLifetime
{
    public const string PublishDirVariable = "DIT_RELEASE_PUBLISH_DIR";
    public const string ConnectionVariable = "DIT_RELEASE_CONNECTION";

    private const int Port = 5080;

    /// <summary>The second Kestrel URL the internal endpoints answer on; the browser tests never use it.</summary>
    private const int InternalPort = 5081;

    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<IPage, ConcurrentQueue<string>> _violations = new();
    private FakeTelegramOidcServer? _telegram;
    private Process? _app;
    private StreamWriter? _appLog;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private long _nextTelegramId;

    public PublishedAppFixture()
    {
        PublishDirectory = Environment.GetEnvironmentVariable(PublishDirVariable) is { Length: > 0 } dir ? Path.GetFullPath(dir) : null;
        ConnectionString = Environment.GetEnvironmentVariable(ConnectionVariable) is { Length: > 0 } cs ? cs : null;

        // Ids unique per run: the database outlives a run (CI migrates it once, a developer reuses theirs).
        _nextTelegramId = 800_000_000_000L + (TimeProvider.System.GetUtcNow().ToUnixTimeSeconds() % 100_000_000L) * 100L;
    }

    public Uri BaseAddress { get; } = new($"http://localhost:{Port.ToString(CultureInfo.InvariantCulture)}/");

    /// <summary>The second Kestrel endpoint, the only one the internal endpoints answer on.</summary>
    public Uri InternalAddress { get; } = new($"http://localhost:{InternalPort.ToString(CultureInfo.InvariantCulture)}/");

    public string? PublishDirectory { get; }

    public string? ConnectionString { get; }

    public bool IsAvailable => PublishDirectory is not null && ConnectionString is not null;

    public IBrowser Browser => _browser ?? throw new InvalidOperationException("The fixture is not started.");

    /// <summary>The published static asset endpoints: fingerprinted route to its unfingerprinted name.</summary>
    public StaticAssetManifest Assets { get; private set; } = StaticAssetManifest.Empty;

    /// <summary>Where the run writes its measurements (<c>TestResults/perf</c> at the repository root).</summary>
    public static string PerfDirectory => Path.Combine(ContentWorkspace.RepositoryRoot(), "TestResults", "perf");

    /// <summary>Skips the calling test when the published build or the database is not configured.</summary>
    public void SkipUnlessAvailable() => Assert.SkipUnless(
        IsAvailable,
        $"Set {PublishDirVariable} (a dotnet publish -c Release output) and {ConnectionVariable} (a migrated database) to run the Release suite.");

    public long NextTelegramId() => Interlocked.Increment(ref _nextTelegramId);

    public async ValueTask InitializeAsync()
    {
        if (!IsAvailable)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(PerfDirectory);
            File.Delete(PerfRecorder.FilePath);
            Assets = StaticAssetManifest.Load(PublishDirectory!);
            _telegram = await FakeTelegramOidcServer.StartAsync(TrainerWebFactoryOptions.TelegramClientId);
            StartApp(_telegram);
            await WaitUntilReadyAsync(TestContext.Current.CancellationToken);
            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }
        catch
        {
            // xUnit does not call DisposeAsync when InitializeAsync throws, so the child process would outlive the run.
            await DisposeAsync();
            throw;
        }
    }

    private void StartApp(FakeTelegramOidcServer telegram)
    {
        string dll = Path.Combine(PublishDirectory!, "Trainer.Web.dll");
        if (!File.Exists(dll))
        {
            throw new FileNotFoundException($"{PublishDirVariable} does not contain Trainer.Web.dll.", dll);
        }

        ProcessStartInfo start = new("dotnet")
        {
            // The content root, and with it wwwroot and the static asset manifest, is the working directory.
            WorkingDirectory = PublishDirectory!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(dll);

        IDictionary<string, string?> env = start.Environment;
        // Inherited from a dotnet watch or IDE session these would inject the hot reload script into the pages under test.
        env.Remove("ASPNETCORE_HOSTINGSTARTUPASSEMBLIES");
        env.Remove("DOTNET_STARTUP_HOOKS");
        env.Remove("DOTNET_MODIFIABLE_ASSEMBLIES");
        env["ASPNETCORE_ENVIRONMENT"] = "Testing";
        env["ASPNETCORE_URLS"] = BaseAddress.GetLeftPart(UriPartial.Authority) + ";http://localhost:" + InternalPort.ToString(CultureInfo.InvariantCulture);
        env["App__Internal__Port"] = InternalPort.ToString(CultureInfo.InvariantCulture);
        env["ContentImport__Token"] = TrainerWebFactory.ImportToken;
        env["ConnectionStrings__Trainer"] = ConnectionString;
        env["App__ContentRoot"] = Path.Combine(ContentWorkspace.RepositoryRoot(), "content");
        env["App__PublicBaseUrl"] = BaseAddress.GetLeftPart(UriPartial.Authority);
        env["App__RateLimits__WritesPerMinute"] = TrainerWebFactory.RaisedRateLimit;
        env["App__RateLimits__ReadsPerMinute"] = TrainerWebFactory.RaisedRateLimit;
        env["App__RateLimits__SearchPerMinute"] = TrainerWebFactory.RaisedRateLimit;
        env["TelegramOidc__ClientId"] = telegram.ClientId;
        env["TelegramOidc__ClientSecret"] = telegram.ClientSecret;
        env["TelegramOidc__MetadataAddress"] = telegram.MetadataAddress.ToString();
        env["Logging__LogLevel__Default"] = "Warning";

        string logDirectory = Path.Combine(ContentWorkspace.RepositoryRoot(), "TestResults", "release");
        Directory.CreateDirectory(logDirectory);
        _appLog = new StreamWriter(Path.Combine(logDirectory, "app.log"), append: false) { AutoFlush = true };
        _app = new Process { StartInfo = start };
        _app.OutputDataReceived += (_, e) => WriteLog(e.Data);
        _app.ErrorDataReceived += (_, e) => WriteLog(e.Data);
        _app.Start();
        _app.BeginOutputReadLine();
        _app.BeginErrorReadLine();
    }

    private void WriteLog(string? line)
    {
        if (line is null || _appLog is not { } log)
        {
            return;
        }

        lock (log)
        {
            log.WriteLine(line);
        }
    }

    private async Task WaitUntilReadyAsync(CancellationToken ct)
    {
        using HttpClient client = new() { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(5) };
        Stopwatch elapsed = Stopwatch.StartNew();
        string last = "no response";
        while (elapsed.Elapsed < ReadyTimeout)
        {
            if (_app!.HasExited)
            {
                throw new InvalidOperationException($"The published app exited with code {_app.ExitCode}; see TestResults/release/app.log.");
            }

            try
            {
                using HttpResponseMessage response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), ct);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }

                last = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
            }
            catch (HttpRequestException exception)
            {
                last = exception.Message;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                last = "timeout";
            }

            await Task.Delay(250, ct);
        }

        throw new TimeoutException($"/health/ready of the published app was not 200 within {ReadyTimeout.TotalSeconds:0} s (last: {last}).");
    }

    /// <summary>A browser context on the published app; <paramref name="mobile"/> applies the measured phone's viewport and touch.</summary>
    public async Task<IBrowserContext> NewContextAsync(string locale = "en-US", string? timezoneId = null, bool mobile = false)
    {
        BrowserNewContextOptions options = new()
        {
            BaseURL = BaseAddress.ToString(),
            Locale = locale,
            TimezoneId = timezoneId,
            ViewportSize = mobile ? new ViewportSize { Width = MobileProfile.Width, Height = MobileProfile.Height } : new ViewportSize { Width = 1280, Height = 900 },
            HasTouch = mobile,
        };
        return await Browser.NewContextAsync(options);
    }

    /// <summary>Signs a new Telegram account in through the real OIDC flow and returns its id.</summary>
    public async Task<long> SignInAsync(IBrowserContext context, string name)
    {
        ArgumentNullException.ThrowIfNull(context);
        FakeTelegramOidcServer telegram = _telegram ?? throw new InvalidOperationException("The fixture is not started.");
        long telegramId = NextTelegramId();
        telegram.NextIdentity = new FakeTelegramIdentity(telegramId, name, "release_" + telegramId.ToString(CultureInfo.InvariantCulture));
        IPage tab = await context.NewPageAsync();
        try
        {
            const string returnPath = "/en/about";
            await tab.GotoAsync("/signin-telegram/challenge?returnUrl=" + Uri.EscapeDataString(returnPath));
            await tab.WaitForURLAsync(new Uri(BaseAddress, returnPath).ToString(), new PageWaitForURLOptions { Timeout = (float)SignInTimeout.TotalMilliseconds });
        }
        finally
        {
            await tab.CloseAsync();
        }

        return telegramId;
    }

    /// <summary>A new tab that records every CSP violation of every document it shows.</summary>
    public async Task<IPage> NewPageAsync(IBrowserContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IPage tab = await context.NewPageAsync();
        ConcurrentQueue<string> violations = new();
        _violations[tab] = violations;
        await tab.ExposeFunctionAsync("__ditReportCspViolation", (string violation) => violations.Enqueue(violation));
        await tab.AddInitScriptAsync(
            """
            document.addEventListener('securitypolicyviolation', function (e) {
              window.__ditReportCspViolation(location.pathname + ': ' + e.violatedDirective + ' blocked ' + (e.blockedURI || 'inline') + ' at ' + e.sourceFile + ':' + e.lineNumber);
            });
            """);
        return tab;
    }

    public void AssertNoCspViolations(IPage page)
    {
        string[] violations = _violations.TryGetValue(page, out ConcurrentQueue<string>? queue)
            ? [.. queue]
            : throw new InvalidOperationException("The page was not created with NewPageAsync.");
        Assert.True(violations.Length == 0, "CSP violations: " + string.Join(" | ", violations));
    }

    /// <summary>Runs a scalar count query against the app's database.</summary>
    public async Task<int> CountAsync(string sql, long telegramUserId)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync(ct);
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("tg", telegramUserId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    /// <summary>Waits up to 30 seconds for <paramref name="sql"/> (a count with a <c>@tg</c> parameter) to return <paramref name="expected"/>.</summary>
    public async Task WaitForCountAsync(string sql, long telegramUserId, int expected)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        int last;
        while ((last = await CountAsync(sql, telegramUserId)) != expected)
        {
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(30), $"Expected {expected} from \"{sql}\" for {telegramUserId}, found {last}.");
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Client answer ids, question ids and modes of the answers the server stored for the user.</summary>
    public async Task<IReadOnlyList<(string ClientAnswerId, string QuestionId, short Mode)>> StoredAnswersAsync(long telegramUserId)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync(ct);
        await using NpgsqlCommand command = new(
            "SELECT a.client_answer_id, a.question_id, a.mode FROM answers a JOIN users u ON u.id = a.user_id WHERE u.telegram_user_id = @tg",
            connection);
        command.Parameters.AddWithValue("tg", telegramUserId);
        List<(string, string, short)> rows = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add((reader.GetString(0).Trim(), reader.GetString(1).Trim(), reader.GetInt16(2)));
        }

        return rows;
    }

    /// <summary>Stores a finished mock interview for the user, as the mock API would.</summary>
    /// <remarks>
    /// Raw SQL rather than the app's services: the published app runs in its own process, and this assembly does not
    /// reference Infrastructure's internal entities. The columns mirror <c>MockSessionEntity</c> (<c>level</c> is the
    /// stored <c>Level</c> value) and the summary mirrors <c>MockSummaryDto</c>/<c>MockTopicResultDto</c>; a schema change
    /// there must be repeated here, and this test is where it shows.
    /// </remarks>
    public async Task AddMockSessionAsync(long telegramUserId, string id, int questionCount, DateTimeOffset finishedAt)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync(ct);
        await using NpgsqlCommand command = new(
            """
            INSERT INTO mock_sessions (id, user_id, stack_key, level, question_count, timed, started_at, finished_at, summary)
            SELECT @id, u.id, 'dotnet', 2, @count, false, @finished - interval '10 minutes', @finished,
                   '{"topics": [{"knew": 1, "topic": "async", "partial": 1, "didntKnow": 1, "unanswered": 0}]}'::jsonb
            FROM users u WHERE u.telegram_user_id = @tg
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("count", questionCount);
        command.Parameters.AddWithValue("finished", finishedAt);
        command.Parameters.AddWithValue("tg", telegramUserId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(ct));
    }

    /// <summary>Makes every review state of the user due now, as if its interval had passed.</summary>
    public async Task MakeReviewsDueAsync(long telegramUserId)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync(ct);
        await using NpgsqlCommand command = new(
            "UPDATE review_states r SET due_at = now() - interval '1 day' FROM users u WHERE u.id = r.user_id AND u.telegram_user_id = @tg",
            connection);
        command.Parameters.AddWithValue("tg", telegramUserId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.CloseAsync();
        }

        _playwright?.Dispose();
        if (_app is not null)
        {
            if (!_app.HasExited)
            {
                _app.Kill(entireProcessTree: true);
                await _app.WaitForExitAsync();
            }

            _app.Dispose();
        }

        if (_telegram is not null)
        {
            await _telegram.DisposeAsync();
        }

        if (_appLog is not null)
        {
            await _appLog.DisposeAsync();
        }
    }
}

/// <summary>
/// The published <c>Trainer.Web.staticwebassets.endpoints.json</c>: which route is a fingerprinted copy of which file,
/// so two requests for one file under different names are recognised.
/// </summary>
public sealed class StaticAssetManifest
{
    private readonly Dictionary<string, string> _labels;

    private StaticAssetManifest(Dictionary<string, string> labels) => _labels = labels;

    public static StaticAssetManifest Empty { get; } = new(new Dictionary<string, string>(StringComparer.Ordinal));

    public static StaticAssetManifest Load(string publishDirectory)
    {
        string path = Path.Combine(publishDirectory, "Trainer.Web.staticwebassets.endpoints.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        Dictionary<string, string> labels = new(StringComparer.Ordinal);
        foreach (JsonElement endpoint in document.RootElement.GetProperty("Endpoints").EnumerateArray())
        {
            foreach (JsonElement property in endpoint.GetProperty("EndpointProperties").EnumerateArray())
            {
                if (property.GetProperty("Name").GetString() == "label")
                {
                    labels["/" + endpoint.GetProperty("Route").GetString()] = "/" + property.GetProperty("Value").GetString();
                }
            }
        }

        return new StaticAssetManifest(labels);
    }

    /// <summary>The unfingerprinted path of <paramref name="path"/> (itself when it is not a fingerprinted route).</summary>
    public string Logical(string path) => _labels.TryGetValue(path, out string? label) ? label : path;

    /// <summary>True when <paramref name="path"/> is a fingerprinted route.</summary>
    public bool IsFingerprinted(string path) => _labels.ContainsKey(path);
}
