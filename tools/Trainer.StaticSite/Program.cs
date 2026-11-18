namespace Trainer.StaticSite;

/// <summary>What the command line touches outside the process, replaceable by tests.</summary>
public sealed class ToolServices
{
    /// <summary>The handler the crawler talks to the app through. Redirects must not be followed.</summary>
    public Func<HttpMessageHandler> CrawlHandler { get; init; } = () => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
    };

    /// <summary>The handler retention talks to the live site through.</summary>
    public Func<HttpMessageHandler> RetentionHandler { get; init; } = () => new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        // A redirect could lead anywhere; the live site answers these files directly.
        AllowAutoRedirect = false,
        UseCookies = false,
    };

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = (span, ct) => Task.Delay(span, ct);

    public TextWriter Out { get; init; } = Console.Out;

    public TextWriter Error { get; init; } = Console.Error;
}

/// <summary>
/// <c>export</c>, <c>verify</c> and <c>serve</c>. Exit codes: 0 success, 1 failure (a crawl, retention or verification problem),
/// 2 usage.
/// </summary>
public static class Program
{
    public const string Usage = """
        Usage:
          export --app <url> --site-url <url> --out <dir> --build-id <id> --commit <sha>
                 [--retain-from <live site url> --live-build-id <id> [--previous-page-out <file>]]
          verify --dir <dir> --site-url <url>
          serve  --dir <dir> --site-url <url> [--port <n>] [--gzip]
        """;

    public static async Task<int> Main(string[] args)
    {
        using CancellationTokenSource cancel = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };
        return await RunAsync(args, new ToolServices(), cancel.Token);
    }

    public static async Task<int> RunAsync(string[] args, ToolServices services, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(services);
        Dictionary<string, string>? options = null;
        string? usageError = args.Length == 0 ? "A command is required." : null;
        if (usageError is null && !TryParse(args[1..], out options, out usageError))
        {
            return await UsageAsync(services, usageError ?? "Invalid arguments.");
        }

        if (usageError is not null)
        {
            return await UsageAsync(services, usageError);
        }

        try
        {
            return args[0] switch
            {
                "export" => await ExportAsync(options!, services, ct),
                "verify" => await VerifyAsync(options!, services),
                "serve" => await ServeAsync(options!, services, ct),
                _ => await UsageAsync(services, $"Unknown command '{args[0]}'."),
            };
        }
        catch (UsageException ex)
        {
            return await UsageAsync(services, ex.Message);
        }
        catch (ExportFailedException ex)
        {
            await services.Error.WriteLineAsync(ex.Message);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            await services.Error.WriteLineAsync(ex.Message);
            return 1;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await services.Error.WriteLineAsync("Cancelled.");
            return 1;
        }
    }

    private static async Task<int> UsageAsync(ToolServices services, string message)
    {
        await services.Error.WriteLineAsync(message);
        await services.Error.WriteLineAsync(Usage);
        return 2;
    }

    private static bool TryParse(string[] args, out Dictionary<string, string>? options, out string? error)
    {
        options = new Dictionary<string, string>(StringComparer.Ordinal);
        error = null;
        for (int i = 0; i < args.Length; i++)
        {
            string name = args[i];
            if (!name.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Unexpected argument '{name}'.";
                return false;
            }

            if (name == "--gzip")
            {
                options[name] = "true";
                continue;
            }

            if (i + 1 >= args.Length)
            {
                error = $"{name} needs a value.";
                return false;
            }

            options[name] = args[++i];
        }

        return true;
    }

    private static string Require(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out string? value) && value.Length > 0 ? value : throw new UsageException($"{name} is required.");

    private static Uri RequireUrl(Dictionary<string, string> options, string name)
    {
        string value = Require(options, name);
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && uri.Scheme is "http" or "https"
            && uri.Query.Length == 0
            && uri.Fragment.Length == 0
            && uri.UserInfo.Length == 0
                ? uri
                : throw new UsageException($"{name} must be an absolute http or https URL without credentials, a query or a fragment.");
    }

    private static async Task<int> ExportAsync(Dictionary<string, string> options, ToolServices services, CancellationToken ct)
    {
        Uri app = RequireUrl(options, "--app");
        Uri site = RequireUrl(options, "--site-url");
        string outDir = Require(options, "--out");
        string buildId = Require(options, "--build-id");
        string commit = Require(options, "--commit");
        RetentionOptions? retention = null;
        if (options.ContainsKey("--retain-from"))
        {
            retention = new RetentionOptions(
                RequireUrl(options, "--retain-from"),
                Require(options, "--live-build-id"),
                options.GetValueOrDefault("--previous-page-out"));
        }
        else if (options.ContainsKey("--live-build-id") || options.ContainsKey("--previous-page-out"))
        {
            throw new UsageException("--live-build-id and --previous-page-out need --retain-from.");
        }

        HttpMessageHandler crawlHandler = services.CrawlHandler();
        try
        {
            await new SiteExporter(crawlHandler, services.Time, services.Delay).ExportAsync(new ExportOptions(app, site, outDir, buildId, commit), ct);
        }
        finally
        {
            crawlHandler.Dispose();
        }

        if (retention is not null)
        {
            HttpMessageHandler retentionHandler = services.RetentionHandler();
            try
            {
                await new Retention(retentionHandler, services.Time, services.Delay).ApplyAsync(retention, outDir, ct);
            }
            finally
            {
                retentionHandler.Dispose();
            }
        }

        await services.Out.WriteLineAsync($"Exported {site} to {Path.GetFullPath(outDir)}.");
        return 0;
    }

    private static async Task<int> VerifyAsync(Dictionary<string, string> options, ToolServices services)
    {
        string dir = Require(options, "--dir");
        Uri site = RequireUrl(options, "--site-url");
        IReadOnlyList<string> problems = ExportVerifier.Verify(dir, site);
        foreach (string problem in problems)
        {
            await services.Error.WriteLineAsync(problem);
        }

        if (problems.Count > 0)
        {
            await services.Error.WriteLineAsync($"{problems.Count} problem(s) found in {Path.GetFullPath(dir)}.");
            return 1;
        }

        await services.Out.WriteLineAsync($"{Path.GetFullPath(dir)} is sound.");
        return 0;
    }

    private static async Task<int> ServeAsync(Dictionary<string, string> options, ToolServices services, CancellationToken ct)
    {
        string dir = Require(options, "--dir");
        Uri site = RequireUrl(options, "--site-url");
        int port = 0;
        if (options.TryGetValue("--port", out string? text) && (!int.TryParse(text, out port) || port is < 0 or > 65535))
        {
            throw new UsageException("--port must be a number from 0 to 65535.");
        }

        await using PagesEmulator emulator = await PagesEmulator.StartAsync(new PagesEmulatorOptions(dir, site, port, options.ContainsKey("--gzip")));
        await services.Out.WriteLineAsync($"Serving {Path.GetFullPath(dir)} at {emulator.BaseAddress}. Press Ctrl+C to stop.");
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C is the way to stop.
        }

        return 0;
    }

    private sealed class UsageException(string message) : Exception(message);
}
