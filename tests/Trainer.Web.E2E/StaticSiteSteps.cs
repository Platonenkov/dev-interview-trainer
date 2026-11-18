using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Playwright;
using Trainer.TestSupport;

namespace Trainer.Web.E2E;

/// <summary>What the static-site tests do in a tab more than once: wait for an island, answer a question, read storage.</summary>
internal static class StaticSiteSteps
{
    public const string ReadyIsland = ".island-root[data-island-ready='true']";
    public const string AnonymousOutbox = "dit.outbox.v1.anon";

    // Questions of the web fixture content. The open ones are self-assessed, so a test can answer them without knowing
    // their options.
    public const string ClosureId = "01KBZ3Q8N9V2K3M4P5R6S7T8WA";
    public const string SelfAssessedAsyncId = "01KBZ3Q8N9V2K3M4P5R6S7T8WB";
    public const string SingleId = "01KBZ3Q8N9V2K3M4P5R6S7T8WC";
    public const string EnglishOnlyAsyncId = "01KBZ3Q8N9V2K3M4P5R6S7T8XA";

    /// <summary>Generous: a cold start of the Debug build under a loaded machine has taken well over ten seconds.</summary>
    public static readonly float IslandTimeoutMs = 90_000;

    /// <summary>
    /// A question on screen. <c>.qcard</c> alone also matches the loading skeleton (<c>qcard qcard--skeleton</c>), which is on
    /// screen for as long as the next question loads: a test that acts on it the moment it appears finds none of the question's
    /// controls, and how often that happens depends on how loaded the machine is.
    /// </summary>
    public const string Question = ".qcard:not(.qcard--skeleton)";

    /// <summary>
    /// Polls an async page function until it returns true. <c>WaitForFunctionAsync</c> cannot: it resolves on the first call of
    /// a function that returns a promise, whatever the promise holds, so it checks once and passes.
    /// </summary>
    public static async Task WaitUntilTrueAsync(IPage tab, string asyncFunction)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(IslandTimeoutMs);
        while (!await tab.EvaluateAsync<bool>(asyncFunction))
        {
            Assert.True(DateTime.UtcNow < deadline, "The page function never returned true: " + asyncFunction);
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    public static async Task WaitForIslandAsync(IPage tab) =>
        await tab.Locator(ReadyIsland).WaitForAsync(new LocatorWaitForOptions { Timeout = IslandTimeoutMs });

    public static async Task<IPage> OpenIslandAsync(StaticSiteFixture site, IBrowserContext context, string relativeUrl)
    {
        IPage tab = await site.NewPageAsync(context);
        await tab.GotoAsync(site.Url(relativeUrl).ToString());
        await WaitForIslandAsync(tab);
        return tab;
    }

    /// <summary>A text of the English client resources, so the tests do not repeat the wording.</summary>
    public static string Resource(string key)
    {
        string path = Path.Combine(ContentWorkspace.RepositoryRoot(), "src", "Trainer.Web.Client", "Localization", "ClientResource.en.resx");
        return XDocument.Load(path).Descendants("data").Single(d => (string?)d.Attribute("name") == key).Element("value")!.Value;
    }

    /// <summary>
    /// Writes the anonymous outbox before the page's own scripts run, once: a later document of the same context keeps
    /// whatever the island wrote since.
    /// </summary>
    public static Task SeedOutboxAsync(IBrowserContext context, string outboxJson) =>
        context.AddInitScriptAsync(
            $"try {{ if (localStorage.getItem('{AnonymousOutbox}') === null) localStorage.setItem('{AnonymousOutbox}', {JsonSerializer.Serialize(outboxJson)}); }} catch (e) {{ }}");

    public static string OutboxJson(IEnumerable<(string Id, string QuestionId, string Grade, DateTimeOffset At)> answers)
    {
        IEnumerable<string> rows = answers.Select(a =>
            $"{{\"clientAnswerId\":\"{a.Id}\",\"questionId\":\"{a.QuestionId}\",\"grade\":\"{a.Grade}\",\"usedHint\":false,\"mode\":\"practice\",\"answeredAt\":\"{a.At.ToString("O", CultureInfo.InvariantCulture)}\"}}");
        return "{\"version\":1,\"answers\":[" + string.Join(',', rows) + "]}";
    }

    /// <summary>The ids of the answers the anonymous outbox holds.</summary>
    public static async Task<string[]> OutboxQuestionIdsAsync(IPage tab) =>
        await tab.EvaluateAsync<string[]>(
            $"() => {{ const v = localStorage.getItem('{AnonymousOutbox}'); return v ? JSON.parse(v).answers.map(a => a.questionId) : []; }}");

    public static async Task<int> SavedAsync(IPage tab) =>
        int.Parse(await tab.Locator(".qs").GetAttributeAsync("data-answers-saved") ?? "0", CultureInfo.InvariantCulture);

    /// <summary>Answers whatever question is on screen and waits for the outbox write and the feedback.</summary>
    public static async Task AnswerShownAsync(IPage tab)
    {
        await tab.Locator(Question).WaitForAsync(new LocatorWaitForOptions { Timeout = IslandTimeoutMs });
        int saved = await SavedAsync(tab);
        if (await tab.Locator(".opt--pick").CountAsync() > 0)
        {
            await tab.Locator(".opt--pick").First.ClickAsync();
            await tab.Locator(".practice__answer").ClickAsync();
        }
        else if (await tab.Locator(".code-line").CountAsync() > 0)
        {
            await tab.Locator(".code-line").First.ClickAsync();
            await tab.Locator(".practice__answer").ClickAsync();
        }
        else if (await tab.Locator(".typed__input").CountAsync() > 0)
        {
            await tab.Locator(".typed__input").FillAsync("x");
            await tab.Locator(".practice__answer").ClickAsync();
        }
        else
        {
            await tab.Locator(".self > button.btn").ClickAsync();
            await tab.Locator(".self__grade--knew").ClickAsync();
        }

        await tab.Locator($".qs[data-answers-saved='{(saved + 1).ToString(CultureInfo.InvariantCulture)}']")
            .WaitForAsync(new LocatorWaitForOptions { Timeout = IslandTimeoutMs });
        await tab.Locator(".feedback").WaitForAsync();
    }

    /// <summary>The paths of the content files (index and question shards) a tab asked for, in order.</summary>
    public static string[] DataRequests(StaticSiteFixture site, IPage tab) =>
        [.. site.RequestUrls(tab).Select(url => new Uri(url).AbsolutePath).Where(path => path.StartsWith(StaticSiteFixture.Prefix + "data/", StringComparison.Ordinal))];

    /// <summary>Waits until a worker controls the page (the worker claims it on activation, so the first visit gets there late).</summary>
    public static async Task WaitForServiceWorkerControlAsync(IPage tab) =>
        await tab.WaitForFunctionAsync(
            "() => navigator.serviceWorker && navigator.serviceWorker.controller !== null",
            null,
            new PageWaitForFunctionOptions { Timeout = IslandTimeoutMs, PollingInterval = 50 });

    /// <summary>The site-root-relative path of the one file in <paramref name="folder"/> of a build whose name is <paramref name="stem"/>, a fingerprint and <paramref name="extension"/>.</summary>
    public static string FingerprintedFile(StaticBuild build, string folder, string stem, string extension)
    {
        System.Text.RegularExpressions.Regex pattern = new(
            "^" + System.Text.RegularExpressions.Regex.Escape(stem) + @"\.[a-z0-9]{10}" + System.Text.RegularExpressions.Regex.Escape(extension) + "$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        string file = Directory.EnumerateFiles(Path.Combine(build.Directory, folder)).Select(Path.GetFileName).Single(name => pattern.IsMatch(name!))!;
        return folder + "/" + file;
    }

    public static async Task<string?> PageBuildAsync(IPage tab) =>
        await tab.Locator("meta[name='dit-build']").GetAttributeAsync("content");
}
