using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Trainer.Contracts;

namespace Trainer.Web.Release;

/// <summary>
/// The static edition's islands under the emulated phone (<see cref="MobileProfile"/>), held to regression budgets as the
/// server edition's are (ADR 0014): the transfer budget always blocks, the timings fail only with <c>DIT_PERF_ENFORCE=1</c>, and
/// every number goes to <c>TestResults/perf/perf.json</c> with <c>edition: static</c>. "Usable" here is the moment the index
/// and the progress replay are done (<c>data-island-ready</c>), "shell" the moment the island first rendered. Bytes are the
/// ones the host sent, which includes what the service worker's fill asked for after the load.
/// </summary>
[Trait("Category", "StaticSmoke")]
public sealed class StaticIslandPerformanceTests(StaticSiteFolder site)
{
    /// <summary>2.5 MB of runtime is about 2.2 s at 9 Mbit/s; it blocks every run (ADR 0014).</summary>
    private const long TransferBudgetBytes = 2_500_000;
    private const double ColdBudgetMs = 8500;
    private const double WarmBudgetMs = 4750;
    private const int SeededAnswers = 10_000;
    private const string PracticePath = "en/dotnet/practice/";

    /// <summary>
    /// How much earlier than the progress a ?first= question must be on screen. The test's browser context has the answers but
    /// no checkpoint, so the page replays all of them first (several seconds on the emulated phone).
    /// </summary>
    private const double MinimumLeadMs = 1000;

    // performance.now() is measured from the document's time origin, its navigation start.
    private const string ReadyClock =
        """
        (() => {
          const mark = () => {
            if (window.__ditReadyAt === undefined && document.querySelector(".island-root[data-island-ready='true']")) {
              window.__ditReadyAt = performance.now();
            }
            if (window.__ditCardAt === undefined && document.querySelector('.qcard')) {
              window.__ditCardAt = performance.now();
            }
            if (window.__ditKnownAt === undefined && document.querySelector('.side__known')) {
              window.__ditKnownAt = performance.now();
            }
          };
          new MutationObserver(mark).observe(document, { subtree: true, childList: true, attributes: true });
        })();
        """;

    // Holds the first look at the answer log for four seconds, without using the CPU. The replay is .NET code on the one thread
    // of the page, so a replay that is already running keeps even a question that is ready from being drawn; holding it
    // before it starts is what lets the test ask whether the question waits for the progress or only for the thread.
    private const string HoldTheLogRead =
        """
        document.addEventListener('DOMContentLoaded', function () {
          ['read', 'fingerprint'].forEach(function (name) {
            var original = window.ditStore[name];
            window.ditStore[name] = function (key) {
              var args = arguments;
              var self = this;
              return key === 'dit.outbox.v1.anon'
                ? new Promise(function (resolve) { setTimeout(resolve, 4000); }).then(function () { return original.apply(self, args); })
                : original.apply(self, args);
            };
          });
        });
        """;

    private static readonly float IslandTimeout = 120_000;

    private static int Runs =>
        int.TryParse(Environment.GetEnvironmentVariable("DIT_PERF_RUNS"), NumberStyles.None, CultureInfo.InvariantCulture, out int runs) && runs > 0 ? runs : 5;

    [Fact]
    public async Task Practice_ColdAndWarm_WithinRegressionBudgets()
    {
        site.SkipUnlessAvailable();
        List<Timing> cold = [];
        long frameworkBytes = 0;
        long contentBytes = 0;
        int frameworkFiles = 0;
        for (int i = 0; i < Runs; i++)
        {
            site.Reset();
            await using IBrowserContext context = await site.NewContextAsync(mobile: true);
            (IPage tab, NetworkRecorder network) = await OpenMeasuredTabAsync(context);

            cold.Add(await TimeToUsableAsync(tab, PracticePath));
            await WaitForQuietAsync();

            // Judged after the numbers are written, so a run that is over budget still leaves them behind. The bytes and the file
            // count recorded are those of the same run, the one that transferred the most.
            long runBytes = BytesUnder("_framework/");
            if (runBytes >= frameworkBytes)
            {
                frameworkBytes = runBytes;
                frameworkFiles = site.Served.Count(request => request.Path.StartsWith(site.Prefix + "_framework/", StringComparison.Ordinal));
            }

            contentBytes = Math.Max(contentBytes, BytesUnder("data/"));
            Assert.All(
                network.Requests.Where(r => r.Path.StartsWith(site.Prefix + "_framework/", StringComparison.Ordinal) && r.Status is not null),
                r => Assert.True(r.Status < 400, $"{r.Path} answered {r.Status}"));
            site.AssertNoCspViolations(tab);
        }

        List<Timing> warm = [];
        site.Reset();
        await using (IBrowserContext context = await site.NewContextAsync(mobile: true))
        {
            (IPage tab, _) = await OpenMeasuredTabAsync(context);
            await TimeToUsableAsync(tab, PracticePath);
            await WaitForQuietAsync();
            for (int i = 0; i < Runs; i++)
            {
                // Through a public page, as a visitor comes back: a full load of the island page.
                await tab.GotoAsync(site.Url("en/about/").ToString());
                warm.Add(await TimeToUsableAsync(tab, PracticePath));
            }

            site.AssertNoCspViolations(tab);
        }

        double coldMedian = PerfRecorder.Median([.. cold.Select(t => t.UsableMs)]);
        double warmMedian = PerfRecorder.Median([.. warm.Select(t => t.UsableMs)]);
        double coldShell = PerfRecorder.Median([.. cold.Select(t => t.ShellMs)]);
        long indexBytes = IndexBytes();
        Record(
            "static.practiceInteractive.en",
            new JsonObject
            {
                ["coldMs"] = Array(cold.Select(t => t.UsableMs)),
                ["warmMs"] = Array(warm.Select(t => t.UsableMs)),
                ["coldMedianMs"] = Math.Round(coldMedian),
                ["warmMedianMs"] = Math.Round(warmMedian),
                ["coldShellMedianMs"] = Math.Round(coldShell),
                ["coldBudgetMs"] = ColdBudgetMs,
                ["warmBudgetMs"] = WarmBudgetMs,
                ["frameworkTransferBytes"] = frameworkBytes,
                ["frameworkFiles"] = frameworkFiles,
                ["contentTransferBytes"] = contentBytes,
                ["indexBytes"] = indexBytes,
                ["indexTransferBytes"] = site.Served.Where(request => request.Path.Contains("/index.", StringComparison.Ordinal) && request.Path.Contains("/data/", StringComparison.Ordinal)).Select(request => request.Bytes).DefaultIfEmpty(0).Max(),
                ["budgetBytes"] = TransferBudgetBytes,
            },
            $"### Static Practice usable ({(PerfRecorder.Enforced ? "enforced" : "advisory")})\n\n" +
            $"cold median {PerfRecorder.Ms(coldMedian)} ms (shell {PerfRecorder.Ms(coldShell)} ms; budget {PerfRecorder.Ms(ColdBudgetMs)} ms), warm median {PerfRecorder.Ms(warmMedian)} ms (budget {PerfRecorder.Ms(WarmBudgetMs)} ms); " +
            $"`_framework/` {frameworkBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes in {frameworkFiles} files (budget {TransferBudgetBytes.ToString("N0", CultureInfo.InvariantCulture)}), content {contentBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes, index {indexBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes.\n");

        Assert.True(frameworkBytes <= TransferBudgetBytes, $"_framework transfer {frameworkBytes} bytes exceeds the {TransferBudgetBytes}-byte budget.");
        if (PerfRecorder.Enforced)
        {
            Assert.True(coldMedian < ColdBudgetMs, $"Cold median {PerfRecorder.Ms(coldMedian)} ms is not under {PerfRecorder.Ms(ColdBudgetMs)} ms.");
            Assert.True(warmMedian < WarmBudgetMs, $"Warm median {PerfRecorder.Ms(warmMedian)} ms is not under {PerfRecorder.Ms(WarmBudgetMs)} ms.");
        }
    }

    [Fact]
    public async Task Practice_UsableWaitsForTheIndexAndProgress()
    {
        site.SkipUnlessAvailable();
        site.Reset();
        string firstId = ReadIndex().Questions[0].Id;
        TimeSpan added = TimeSpan.FromSeconds(2);

        // The index is late: the shell can render, but the island is not usable (an empty shell must not pass).
        site.Delay("data/", added);
        await using (IBrowserContext context = await site.NewContextAsync())
        {
            IPage tab = await NewTabAsync(context);
            await tab.GotoAsync(site.Url(PracticePath).ToString());
            await tab.Locator(".island-root[data-island-shell-ms]").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = IslandTimeout });
            await Task.Delay(500, TestContext.Current.CancellationToken);
            Assert.Equal(0, await tab.Locator(".island-root[data-island-ready='true']").CountAsync());
            await WaitForReadyAsync(tab);
            double[] times = await ReadTimesAsync(tab);
            Assert.True(times[0] >= added.TotalMilliseconds, $"usable at {times[0]:0} ms, before the {added.TotalMilliseconds:0} ms the index was held");
            Assert.True(times[0] - times[1] >= 1000, $"usable ({times[0]:0} ms) came with the shell ({times[1]:0} ms)");
            site.AssertNoCspViolations(tab);
        }

        // The first question of a ?first= link is late: that island is usable only once it shows the question.
        site.Reset();
        site.Delay("data/en/q/", added);
        await using (IBrowserContext context = await site.NewContextAsync())
        {
            IPage tab = await NewTabAsync(context);
            await tab.GotoAsync(site.Url(PracticePath + "?first=" + firstId).ToString());
            await WaitForReadyAsync(tab);
            double[] times = await ReadTimesAsync(tab);
            Assert.True(times[0] >= added.TotalMilliseconds, $"usable at {times[0]:0} ms, before the first question's {added.TotalMilliseconds:0} ms");
            Assert.Equal(1, await tab.Locator(".qcard").CountAsync());
            site.AssertNoCspViolations(tab);
        }
    }

    [Fact]
    public async Task Practice_UsableWith10000Answers_WithinBudget()
    {
        site.SkipUnlessAvailable();
        string outbox = SeededOutbox(ReadIndex());

        // The first load of a log that has no checkpoint yet (every visitor's first load after the checkpoint shipped) replays
        // the whole log once and stores the checkpoint; later loads read it. Timed once and recorded, not budgeted.
        site.Reset();
        Timing firstLoad;
        string checkpoint;
        await using (IBrowserContext priming = await site.NewContextAsync(mobile: true, seededOutbox: outbox))
        {
            (IPage tab, _) = await OpenMeasuredTabAsync(priming);
            firstLoad = await TimeToUsableAsync(tab, PracticePath);
            await tab.WaitForFunctionAsync("() => localStorage.getItem('dit.progress.v1.anon') !== null", null, new PageWaitForFunctionOptions { Timeout = IslandTimeout, PollingInterval = 100 });
            checkpoint = await tab.EvaluateAsync<string>("() => localStorage.getItem('dit.progress.v1.anon')");
            Assert.Equal(SeededAnswers, await StoredAnswerCountAsync(tab));
            site.AssertNoCspViolations(tab);
        }

        // Cold is a new browser context, an empty HTTP cache, but the visitor's storage: the log and its checkpoint.
        List<Timing> cold = [];
        for (int i = 0; i < Runs; i++)
        {
            site.Reset();
            await using IBrowserContext context = await site.NewContextAsync(mobile: true, seededOutbox: outbox, seededCheckpoint: checkpoint);
            (IPage tab, _) = await OpenMeasuredTabAsync(context);
            cold.Add(await TimeToUsableAsync(tab, PracticePath));
            Assert.Equal(SeededAnswers, await StoredAnswerCountAsync(tab));
            site.AssertNoCspViolations(tab);
        }

        List<Timing> warm = [];
        double appendMs;
        site.Reset();
        await using (IBrowserContext context = await site.NewContextAsync(mobile: true, seededOutbox: outbox, seededCheckpoint: checkpoint))
        {
            (IPage tab, _) = await OpenMeasuredTabAsync(context);
            await TimeToUsableAsync(tab, PracticePath);
            await WaitForQuietAsync();
            for (int i = 0; i < Runs; i++)
            {
                await tab.GotoAsync(site.Url("en/about/").ToString());
                warm.Add(await TimeToUsableAsync(tab, PracticePath));
            }

            // One answer appended to a log of ten thousand: the time from the click to the outbox write being reported.
            await tab.Locator(".pf__start").ClickAsync();
            await tab.Locator(".qcard").WaitForAsync(new LocatorWaitForOptions { Timeout = IslandTimeout });
            appendMs = await AnswerOneAsync(tab);
            Assert.Equal(SeededAnswers + 1, await StoredAnswerCountAsync(tab));
            site.AssertNoCspViolations(tab);
        }

        double coldMedian = PerfRecorder.Median([.. cold.Select(t => t.UsableMs)]);
        double warmMedian = PerfRecorder.Median([.. warm.Select(t => t.UsableMs)]);
        Record(
            "static.practiceInteractive10000.en",
            new JsonObject
            {
                ["answers"] = SeededAnswers,
                ["coldMs"] = Array(cold.Select(t => t.UsableMs)),
                ["warmMs"] = Array(warm.Select(t => t.UsableMs)),
                ["coldMedianMs"] = Math.Round(coldMedian),
                ["warmMedianMs"] = Math.Round(warmMedian),
                ["coldShellMedianMs"] = Math.Round(PerfRecorder.Median([.. cold.Select(t => t.ShellMs)])),
                ["replayCostMs"] = Math.Round(PerfRecorder.Median([.. warm.Select(t => t.UsableMs - t.ShellMs)])),
                ["firstLoadWithoutCheckpointMs"] = Math.Round(firstLoad.UsableMs),
                ["checkpointBytes"] = checkpoint.Length,
                ["appendMs"] = Math.Round(appendMs),
                ["coldBudgetMs"] = ColdBudgetMs,
                ["warmBudgetMs"] = WarmBudgetMs,
            },
            $"### Static Practice with {SeededAnswers.ToString("N0", CultureInfo.InvariantCulture)} answers ({(PerfRecorder.Enforced ? "enforced" : "advisory")})\n\n" +
            $"usable: cold median {PerfRecorder.Ms(coldMedian)} ms (budget {PerfRecorder.Ms(ColdBudgetMs)} ms), warm median {PerfRecorder.Ms(warmMedian)} ms (budget {PerfRecorder.Ms(WarmBudgetMs)} ms); the one load that replays the log and stores the checkpoint {PerfRecorder.Ms(firstLoad.UsableMs)} ms; checkpoint {checkpoint.Length.ToString("N0", CultureInfo.InvariantCulture)} bytes; one appended answer {PerfRecorder.Ms(appendMs)} ms.\n");

        if (PerfRecorder.Enforced)
        {
            Assert.True(coldMedian < ColdBudgetMs, $"Cold median {PerfRecorder.Ms(coldMedian)} ms is not under {PerfRecorder.Ms(ColdBudgetMs)} ms.");
            Assert.True(warmMedian < WarmBudgetMs, $"Warm median {PerfRecorder.Ms(warmMedian)} ms is not under {PerfRecorder.Ms(WarmBudgetMs)} ms.");
        }
    }

    [Theory]
    [InlineData("all")]
    [InlineData("new")]
    [InlineData("due")]
    public async Task Practice_StartImmediatelyAfterUsable_AllNewDueAndFirst(string status)
    {
        site.SkipUnlessAvailable();
        site.Reset();
        StaticIndexDto index = ReadIndex();
        string outbox = SeededOutbox(index);
        await using IBrowserContext context = await site.NewContextAsync(seededOutbox: outbox);
        IPage tab = await NewTabAsync(context);
        await tab.GotoAsync(site.Url(PracticePath).ToString());
        await WaitForReadyAsync(tab);

        // Pressed at once, with the ten thousand answers replayed: the session starts without waiting for anything else.
        await tab.Locator($".pf input[name='status'][value='{status}']").CheckAsync();
        Stopwatch clock = Stopwatch.StartNew();
        await tab.Locator(".pf__start").ClickAsync();
        await tab.Locator(".qcard").WaitForAsync(new LocatorWaitForOptions { Timeout = IslandTimeout });
        clock.Stop();

        Assert.True(clock.ElapsedMilliseconds < 500, $"Start took {clock.ElapsedMilliseconds} ms to show the first question for '{status}'.");
        Assert.Equal(0, await tab.Locator(".island-state").CountAsync());
        site.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Practice_FirstLink_ShowsItsQuestionWithoutWaitingForTheReplay()
    {
        site.SkipUnlessAvailable();
        site.Reset();
        StaticIndexDto index = ReadIndex();
        string outbox = SeededOutbox(index);
        await using IBrowserContext context = await site.NewContextAsync(mobile: true, seededOutbox: outbox);
        (IPage tab, _) = await OpenMeasuredTabAsync(context);
        await tab.AddInitScriptAsync(HoldTheLogRead);

        await tab.GotoAsync(site.Url(PracticePath + "?first=" + index.Questions[0].Id).ToString());
        await tab.Locator(".qcard").WaitForAsync(new LocatorWaitForOptions { Timeout = IslandTimeout });
        await tab.WaitForFunctionAsync("() => window.__ditKnownAt !== undefined", null, new PageWaitForFunctionOptions { Timeout = IslandTimeout, PollingInterval = 50 });

        double[] marks = await tab.EvaluateAsync<double[]>("() => [window.__ditCardAt, window.__ditKnownAt]");
        // The sidebar's progress comes from the replay; the question is on screen well before it. Equal marks would also pass
        // "no later", which is what a question held back until the replay is done looks like, so a real margin is required.
        Assert.True(marks[1] - marks[0] >= MinimumLeadMs, $"the question appeared at {marks[0]:0} ms and the progress at {marks[1]:0} ms: less than {MinimumLeadMs:0} ms apart");
        Assert.Equal(SeededAnswers, await StoredAnswerCountAsync(tab));
        site.AssertNoCspViolations(tab);
    }

    private async Task<IPage> NewTabAsync(IBrowserContext context)
    {
        IPage tab = await site.NewPageAsync(context);
        await tab.AddInitScriptAsync(ReadyClock);
        return tab;
    }

    private async Task<(IPage Tab, NetworkRecorder Network)> OpenMeasuredTabAsync(IBrowserContext context)
    {
        IPage tab = await NewTabAsync(context);
        NetworkRecorder network = await NetworkRecorder.AttachAsync(context, tab);
        await MobileProfile.ApplyAsync(network.Session);
        return (tab, network);
    }

    private static async Task WaitForReadyAsync(IPage tab) =>
        await tab.WaitForFunctionAsync("() => window.__ditReadyAt !== undefined", null, new PageWaitForFunctionOptions { Timeout = IslandTimeout, PollingInterval = 50 });

    // [usable, shell] in milliseconds since navigation start.
    private static async Task<double[]> ReadTimesAsync(IPage tab) =>
        await tab.EvaluateAsync<double[]>("() => [window.__ditReadyAt, Number(document.querySelector('.island-root').dataset.islandShellMs)]");

    private async Task<Timing> TimeToUsableAsync(IPage tab, string path)
    {
        await tab.GotoAsync(site.Url(path).ToString());
        await WaitForReadyAsync(tab);
        double[] times = await ReadTimesAsync(tab);
        return new Timing(times[0], times[1]);
    }

    // Waits until the host has been quiet for 1.5 s: the worker's fill after the load has finished asking for files.
    private async Task WaitForQuietAsync()
    {
        Stopwatch overall = Stopwatch.StartNew();
        Stopwatch quiet = Stopwatch.StartNew();
        int last = -1;
        while (quiet.Elapsed < TimeSpan.FromSeconds(1.5))
        {
            Assert.True(overall.Elapsed < TimeSpan.FromSeconds(90), "the host kept being asked for files for 90 s");
            int count = site.Served.Count;
            if (count != last)
            {
                last = count;
                quiet.Restart();
            }

            await Task.Delay(250, TestContext.Current.CancellationToken);
        }
    }

    private long BytesUnder(string relative) =>
        site.Served.Where(request => request.Path.StartsWith(site.Prefix + relative, StringComparison.Ordinal)).Sum(request => request.Bytes);

    // A deployment keeps the previous build's immutable files (retained.json), so after a content change the folder holds
    // the old index too; immutable.json lists only this build's files, which is the index the pages point to.
    private string IndexFile()
    {
        string root = site.Inputs!.Directory;
        using JsonDocument immutable = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "immutable.json")));
        string relative = immutable.RootElement.GetProperty("files").EnumerateArray()
            .Select(file => file.GetString() ?? "")
            .Single(file => file.StartsWith("data/en/dotnet/index.", StringComparison.Ordinal)
                && file.EndsWith(".json", StringComparison.Ordinal));
        return Path.Combine(root, relative);
    }

    private StaticIndexDto ReadIndex() =>
        JsonSerializer.Deserialize(File.ReadAllBytes(IndexFile()), TrainerJsonContext.Default.StaticIndexDto)
        ?? throw new InvalidOperationException("The content index is empty.");

    private long IndexBytes() => new FileInfo(IndexFile()).Length;

    // Ten thousand answers over the questions of the site, in order; the tail holds one miss per answered question (at least
    // five hundred) so that every answered question ends up due whatever the size of the bank; every fifth question is never
    // answered, so "not answered yet" has questions to pick from.
    private static string SeededOutbox(StaticIndexDto index)
    {
        string[] ids = [.. index.Questions.Where((_, position) => position % 5 != 4).Select(question => question.Id)];
        Assert.NotEmpty(ids);
        // The tail cycles through the ids in order, so it covers every answered question only when it is at least that long.
        int missTail = Math.Max(500, ids.Length);
        Assert.True(missTail * 2 <= SeededAnswers, $"{ids.Length} answered questions leave too few answers before the miss tail");
        DateTimeOffset end = DateTimeOffset.UtcNow - TimeSpan.FromDays(5);
        TimeSpan span = TimeSpan.FromDays(45);
        StringBuilder json = new("{\"version\":1,\"answers\":[");
        for (int i = 0; i < SeededAnswers; i++)
        {
            string grade = i >= SeededAnswers - missTail ? "didnt-know" : (i % 3) switch { 0 => "knew", 1 => "partial", _ => "didnt-know" };
            DateTimeOffset at = end - span + (span * i / SeededAnswers);
            json.Append(i == 0 ? "" : ",")
                .Append("{\"clientAnswerId\":\"01M").Append(i.ToString("D23", CultureInfo.InvariantCulture))
                .Append("\",\"questionId\":\"").Append(ids[i % ids.Length])
                .Append("\",\"grade\":\"").Append(grade)
                .Append("\",\"usedHint\":false,\"mode\":\"practice\",\"mockSessionId\":null,\"answeredAt\":\"")
                .Append(at.ToString("O", CultureInfo.InvariantCulture)).Append("\"}");
        }

        return json.Append("]}").ToString();
    }

    private static async Task<int> StoredAnswerCountAsync(IPage tab) =>
        await tab.EvaluateAsync<int>("() => JSON.parse(localStorage.getItem('dit.outbox.v1.anon')).answers.length");

    // Answers the question on screen; returns the milliseconds from the click that grades it to the outbox write being reported.
    private static async Task<double> AnswerOneAsync(IPage tab)
    {
        int saved = int.Parse(await tab.Locator(".qs").GetAttributeAsync("data-answers-saved") ?? "0", CultureInfo.InvariantCulture);
        ILocator submit;
        if (await tab.Locator(".opt--pick").CountAsync() > 0)
        {
            await tab.Locator(".opt--pick").First.ClickAsync();
            submit = tab.Locator(".practice__answer");
        }
        else if (await tab.Locator(".code-line").CountAsync() > 0)
        {
            await tab.Locator(".code-line").First.ClickAsync();
            submit = tab.Locator(".practice__answer");
        }
        else if (await tab.Locator(".typed__input").CountAsync() > 0)
        {
            await tab.Locator(".typed__input").FillAsync("x");
            submit = tab.Locator(".practice__answer");
        }
        else
        {
            await tab.Locator(".self > button.btn").ClickAsync();
            submit = tab.Locator(".self__grade--knew");
        }

        Stopwatch clock = Stopwatch.StartNew();
        await submit.ClickAsync();
        await tab.Locator($".qs[data-answers-saved='{(saved + 1).ToString(CultureInfo.InvariantCulture)}']").WaitForAsync(new LocatorWaitForOptions { Timeout = IslandTimeout });
        return clock.Elapsed.TotalMilliseconds;
    }

    private sealed record Timing(double UsableMs, double ShellMs);

    private static JsonArray Array(IEnumerable<double> values) => [.. values.Select(value => (JsonNode)Math.Round(value))];

    private static void Record(string section, JsonObject values, string summary)
    {
        values["edition"] = "static";
        PerfRecorder.Record(section, values, summary);
    }
}
