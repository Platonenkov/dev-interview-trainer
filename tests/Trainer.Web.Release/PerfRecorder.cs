using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Trainer.Web.Release;

/// <summary>
/// Collects the run's measurements in <c>TestResults/perf/perf.json</c> (one section per test) and, on GitHub Actions,
/// appends them to the job summary.
/// </summary>
internal static class PerfRecorder
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly Lock Gate = new();

    public static string FilePath => Path.Combine(PublishedAppFixture.PerfDirectory, "perf.json");

    /// <summary>True when timings must stay within their regression budgets (<c>DIT_PERF_ENFORCE=1</c>); otherwise they are only recorded.</summary>
    public static bool Enforced => string.Equals(Environment.GetEnvironmentVariable("DIT_PERF_ENFORCE"), "1", StringComparison.Ordinal);

    public static void Record(string section, JsonObject values, string summaryMarkdown)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(PublishedAppFixture.PerfDirectory);
            JsonObject root = File.Exists(FilePath)
                ? JsonNode.Parse(File.ReadAllText(FilePath))!.AsObject()
                : Header();
            root[section] = values;
            File.WriteAllText(FilePath, root.ToJsonString(Indented));

            if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
            {
                File.AppendAllText(summary, summaryMarkdown + Environment.NewLine, Encoding.UTF8);
            }
        }
    }

    public static double Median(IReadOnlyList<double> values)
    {
        double[] sorted = [.. values.Order()];
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    public static string Ms(double value) => value.ToString("0", CultureInfo.InvariantCulture);

    private static JsonObject Header() => new()
    {
        ["recordedAt"] = TimeProvider.System.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
        ["commit"] = Environment.GetEnvironmentVariable("GITHUB_SHA"),
        ["runner"] = Environment.GetEnvironmentVariable("RUNNER_NAME") is { Length: > 0 } runner
            ? $"{runner} ({Environment.GetEnvironmentVariable("ImageOS")}, {Environment.ProcessorCount} CPU)"
            : $"local ({Environment.OSVersion}, {Environment.ProcessorCount} CPU)",
        ["enforced"] = Enforced,
        ["profile"] = new JsonObject
        {
            ["latencyMs"] = MobileProfile.LatencyMs,
            ["downloadMbps"] = MobileProfile.DownloadMbps,
            ["uploadMbps"] = MobileProfile.UploadMbps,
            ["cpuSlowdown"] = MobileProfile.CpuSlowdown,
            ["viewport"] = $"{MobileProfile.Width}x{MobileProfile.Height}",
            ["touch"] = true,
        },
    };
}
