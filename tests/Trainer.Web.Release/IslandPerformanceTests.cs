using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace Trainer.Web.Release;

/// <summary>
/// The lab regression guard for the islands (ADR 0014): the published build under <see cref="MobileProfile"/>, held to
/// regression budgets, not to spec 8.2's real-phone targets (2 s warm, 5 s cold), which Plan 3 measures on the deployed
/// site. The transfer budget always blocks; the timing budgets fail only with <c>DIT_PERF_ENFORCE=1</c> (shared runners
/// are noisy), and every number goes to <c>TestResults/perf/perf.json</c>.
/// </summary>
[Trait("Category", "Release")]
public sealed class IslandPerformanceTests(PublishedAppFixture app)
{
    private const int Runs = 5;

    /// <summary>Planner's call: 2.5 MB of runtime is about 2.2 s at 9 Mbit/s; it blocks every run (ADR 0014).</summary>
    private const long TransferBudgetBytes = 2_500_000;
    // Regression budgets for the emulated phone (owner decision 2026-11-13, ADR 0014): above the medians measured after
    // Stage 2 (about 7.7 s cold, 4.3 s warm locally), so a run fails when start-up regresses, not because the emulation
    // is slower than a real phone.
    private const double ColdBudgetMs = 8500;
    private const double WarmBudgetMs = 4750;

    // performance.now() is measured from the document's time origin, its navigation start.
    private const string ReadyClock =
        """
        (() => {
          const mark = () => {
            if (window.__ditReadyAt === undefined && document.querySelector(".island-root[data-island-ready='true']")) {
              window.__ditReadyAt = performance.now();
            }
          };
          new MutationObserver(mark).observe(document, { subtree: true, childList: true, attributes: true, attributeFilter: ['data-island-ready'] });
        })();
        """;

    private static readonly float IslandTimeout = 120_000;

    // Each culture loads its own satellite resources (and, with ICU, its own data shard), so both are measured.
    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task InitialWasmTransfer_WithinBudget(string culture)
    {
        app.SkipUnlessAvailable();
        await using IBrowserContext context = await app.NewContextAsync(mobile: true);
        (IPage tab, NetworkRecorder network) = await OpenMeasuredTabAsync(context);

        await tab.GotoAsync($"/{culture}/dotnet/practice");
        await WaitForReadyAsync(tab);
        await network.WaitForFrameworkSettledAsync();

        RecordedRequest[] framework = [.. network.Framework];
        long total = framework.Sum(r => r.EncodedBytes);
        string[] encodings = [.. framework.Select(r => r.ContentEncoding ?? "identity").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        JsonArray files = [.. framework.OrderByDescending(r => r.EncodedBytes).Select(r => (JsonNode)new JsonObject
        {
            ["path"] = r.Path,
            ["encodedBytes"] = r.EncodedBytes,
            ["encoding"] = r.ContentEncoding,
        })];
        PerfRecorder.Record(
            "transfer." + culture,
            new JsonObject
            {
                ["frameworkEncodedBytes"] = total,
                ["budgetBytes"] = TransferBudgetBytes,
                ["requests"] = framework.Length,
                ["encodings"] = new JsonArray([.. encodings.Select(e => (JsonNode)e)]),
                ["files"] = files,
            },
            $"### Island runtime transfer ({culture})\n\n`_framework/*` on a cold load of `/{culture}/dotnet/practice`: " +
            $"**{total.ToString("N0", CultureInfo.InvariantCulture)} bytes** in {framework.Length} requests ({string.Join(", ", encodings)}); " +
            $"budget {TransferBudgetBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes.\n");

        Assert.All(framework, r => Assert.False(r.FromCache, $"{r.Path} came from a cache on a cold load."));
        Assert.True(total <= TransferBudgetBytes, $"_framework transfer {total} bytes exceeds the {TransferBudgetBytes}-byte budget.");
        app.AssertNoCspViolations(tab);
    }

    // Each culture loads its own satellite resources, so both are timed.
    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task PracticeInteractive_ColdAndWarm_Recorded(string culture)
    {
        app.SkipUnlessAvailable();
        List<Timing> cold = [];
        for (int i = 0; i < Runs; i++)
        {
            await using IBrowserContext context = await app.NewContextAsync(mobile: true);
            (IPage tab, _) = await OpenMeasuredTabAsync(context);
            cold.Add(await TimeToInteractiveAsync(tab, culture));
            app.AssertNoCspViolations(tab);
        }

        List<Timing> warm = [];
        await using (IBrowserContext context = await app.NewContextAsync(mobile: true))
        {
            (IPage tab, _) = await OpenMeasuredTabAsync(context);
            await TimeToInteractiveAsync(tab, culture);
            for (int i = 0; i < Runs; i++)
            {
                // Through a public page, as a visitor comes back: a full page load of the island page (ADR 0009).
                await tab.GotoAsync($"/{culture}/about");
                warm.Add(await TimeToInteractiveAsync(tab, culture));
            }

            app.AssertNoCspViolations(tab);
        }

        double coldMedian = PerfRecorder.Median([.. cold.Select(t => t.InteractiveMs)]);
        double warmMedian = PerfRecorder.Median([.. warm.Select(t => t.InteractiveMs)]);
        double coldLoaded = PerfRecorder.Median([.. cold.Select(t => t.RuntimeLoadedMs)]);
        double warmLoaded = PerfRecorder.Median([.. warm.Select(t => t.RuntimeLoadedMs)]);
        StringBuilder summary = new();
        summary.Append(CultureInfo.InvariantCulture, $"### Practice interactive, {culture} ({(PerfRecorder.Enforced ? "enforced" : "advisory")})\n\n");
        summary.Append("| | median | runs | budget | runtime files loaded (median) |\n|---|---|---|---|---|\n");
        summary.Append(CultureInfo.InvariantCulture, $"| cold | {PerfRecorder.Ms(coldMedian)} ms | {string.Join(", ", cold.Select(t => PerfRecorder.Ms(t.InteractiveMs)))} | < {PerfRecorder.Ms(ColdBudgetMs)} ms | {PerfRecorder.Ms(coldLoaded)} ms |\n");
        summary.Append(CultureInfo.InvariantCulture, $"| warm | {PerfRecorder.Ms(warmMedian)} ms | {string.Join(", ", warm.Select(t => PerfRecorder.Ms(t.InteractiveMs)))} | < {PerfRecorder.Ms(WarmBudgetMs)} ms | {PerfRecorder.Ms(warmLoaded)} ms |\n");
        PerfRecorder.Record(
            "practiceInteractive." + culture,
            new JsonObject
            {
                ["enforced"] = PerfRecorder.Enforced,
                ["coldMs"] = new JsonArray([.. cold.Select(t => (JsonNode)Math.Round(t.InteractiveMs))]),
                ["warmMs"] = new JsonArray([.. warm.Select(t => (JsonNode)Math.Round(t.InteractiveMs))]),
                ["coldMedianMs"] = Math.Round(coldMedian),
                ["warmMedianMs"] = Math.Round(warmMedian),
                ["coldRuntimeLoadedMs"] = new JsonArray([.. cold.Select(t => (JsonNode)Math.Round(t.RuntimeLoadedMs))]),
                ["warmRuntimeLoadedMs"] = new JsonArray([.. warm.Select(t => (JsonNode)Math.Round(t.RuntimeLoadedMs))]),
                ["coldBudgetMs"] = ColdBudgetMs,
                ["warmBudgetMs"] = WarmBudgetMs,
            },
            summary.ToString());

        if (PerfRecorder.Enforced)
        {
            Assert.True(coldMedian < ColdBudgetMs, $"Cold median {PerfRecorder.Ms(coldMedian)} ms is not under {PerfRecorder.Ms(ColdBudgetMs)} ms.");
            Assert.True(warmMedian < WarmBudgetMs, $"Warm median {PerfRecorder.Ms(warmMedian)} ms is not under {PerfRecorder.Ms(WarmBudgetMs)} ms.");
        }
    }

    private async Task<(IPage Tab, NetworkRecorder Network)> OpenMeasuredTabAsync(IBrowserContext context)
    {
        IPage tab = await app.NewPageAsync(context);
        await tab.AddInitScriptAsync(ReadyClock);
        NetworkRecorder network = await NetworkRecorder.AttachAsync(context, tab);
        await MobileProfile.ApplyAsync(network.Session);
        return (tab, network);
    }

    private static async Task<Timing> TimeToInteractiveAsync(IPage tab, string culture)
    {
        await tab.GotoAsync($"/{culture}/dotnet/practice");
        await WaitForReadyAsync(tab);
        double[] values = await tab.EvaluateAsync<double[]>(
            """
            () => {
              const client = performance.getEntriesByType('resource').find(r => r.name.includes('/_framework/Trainer.Web.Client.'));
              return [window.__ditReadyAt, client ? client.responseEnd : 0];
            }
            """);
        return new Timing(values[0], values[1]);
    }

    private static async Task WaitForReadyAsync(IPage tab) =>
        await tab.WaitForFunctionAsync("() => window.__ditReadyAt !== undefined", null, new PageWaitForFunctionOptions { Timeout = IslandTimeout, PollingInterval = 50 });

    /// <param name="RuntimeLoadedMs">When the client assembly, one of the last start-up downloads, arrived.</param>
    private sealed record Timing(double InteractiveMs, double RuntimeLoadedMs);
}
