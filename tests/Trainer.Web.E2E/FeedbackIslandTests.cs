using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Trainer.Domain.Grading;
using Trainer.Infrastructure.Persistence;
using Trainer.TestSupport;

namespace Trainer.Web.E2E;

/// <summary>
/// Rating and reporting a question from Practice in a real browser (ADR 0016): the controls appear for a signed-in user
/// once the answer is graded, store through the API, and never show for an anonymous visitor.
/// </summary>
[Trait("Category", "E2E")]
public sealed class FeedbackIslandTests(AppHostFixture host) : IClassFixture<AppHostFixture>
{
    private const string PracticePath = "/en/dotnet/practice";
    private const string SingleId = "01KBZ3Q8N9V2K3M4P5R6S7T8WC";

    private static readonly TimeSpan IslandTimeout = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<IPage> OpenAnsweredAsync(IBrowserContext context)
    {
        IPage tab = await host.NewPageAsync(context);
        await tab.GotoAsync($"{PracticePath}?first={SingleId}");
        await tab.Locator(".qcard").WaitForAsync(new LocatorWaitForOptions { Timeout = (float)IslandTimeout.TotalMilliseconds });
        await tab.Locator(".opt--pick").First.ClickAsync();
        await tab.Locator(".practice__answer").ClickAsync();
        await tab.Locator(".feedback").WaitForAsync();
        return tab;
    }

    private async Task<int> CountAsync(FormattableString sql)
    {
        await using TrainerDbContext db = PostgresFixture.CreateContext(host.ConnectionString);
        return await db.Database.SqlQuery<int>(sql).SingleAsync(Ct);
    }

    private Task<int> RatingsAsync(long telegramId) => CountAsync(
        $"SELECT count(*)::int AS \"Value\" FROM question_ratings r JOIN users u ON u.id = r.user_id WHERE u.telegram_user_id = {telegramId}");

    private Task<int> ReportsAsync(long telegramId) => CountAsync(
        $"SELECT count(*)::int AS \"Value\" FROM question_reports r JOIN users u ON u.id = r.user_id WHERE u.telegram_user_id = {telegramId}");

    // The status text can say "Saved" for the previous write, so the clear is confirmed in the database.
    private async Task WaitForRatingsAsync(long telegramId, int expected)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (await RatingsAsync(telegramId) != expected)
        {
            Assert.True(DateTime.UtcNow < deadline, $"The ratings of {telegramId} did not reach {expected}.");
            await Task.Delay(100, Ct);
        }
    }

    [Fact]
    public async Task Practice_SignedIn_RatesAndReportsAfterAnswering()
    {
        const long telegramId = 990_700_001L;
        await using IBrowserContext context = await host.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString(),
            Locale = "en-US",
        });
        await host.SignInAsync(context, new FakeTelegramIdentity(telegramId, "Feedback Tester", "feedback_tester"));
        IPage tab = await host.NewPageAsync(context);
        await tab.GotoAsync($"{PracticePath}?first={SingleId}");
        await tab.Locator(".qcard").WaitForAsync(new LocatorWaitForOptions { Timeout = (float)IslandTimeout.TotalMilliseconds });

        // Nothing to rate before the answer.
        Assert.Equal(0, await tab.Locator(".feedback-bar").CountAsync());
        await tab.Locator(".opt--pick").First.ClickAsync();
        await tab.Locator(".practice__answer").ClickAsync();
        await tab.Locator(".feedback-bar").WaitForAsync();

        ILocator like = tab.Locator(".feedback-bar__rate").Nth(0);
        ILocator dislike = tab.Locator(".feedback-bar__rate").Nth(1);
        await Assertions.Expect(like).ToHaveAttributeAsync("aria-pressed", "false");

        await like.ClickAsync();
        await Assertions.Expect(like).ToHaveAttributeAsync("aria-pressed", "true");
        await Assertions.Expect(tab.Locator(".feedback-bar__status")).ToHaveTextAsync("Saved");
        Assert.Equal(1, await RatingsAsync(telegramId));

        // The stored rating comes back with the next session.
        await tab.ReloadAsync();
        await tab.Locator(".qcard").WaitForAsync(new LocatorWaitForOptions { Timeout = (float)IslandTimeout.TotalMilliseconds });
        await tab.Locator(".opt--pick").First.ClickAsync();
        await tab.Locator(".practice__answer").ClickAsync();
        await Assertions.Expect(like).ToHaveAttributeAsync("aria-pressed", "true");

        await dislike.ClickAsync();
        await Assertions.Expect(dislike).ToHaveAttributeAsync("aria-pressed", "true");
        await Assertions.Expect(like).ToHaveAttributeAsync("aria-pressed", "false");

        // Pressing the active rating clears it.
        await dislike.ClickAsync();
        await Assertions.Expect(dislike).ToHaveAttributeAsync("aria-pressed", "false");
        await WaitForRatingsAsync(telegramId, 0);

        await tab.Locator(".feedback-bar__summary").ClickAsync();
        await Assertions.Expect(tab.Locator(".feedback-bar__send")).ToBeDisabledAsync();
        await tab.Locator(".feedback-bar__reason").Nth(1).ClickAsync();
        await tab.Locator(".feedback-bar__comment").FillAsync("The wording of option B is ambiguous.");
        // Tagged before Send: the stored report must replace the element, not just flip a flag on it.
        await tab.Locator(".feedback-bar__report").EvaluateAsync("e => { e.__old = true; }");
        await tab.Locator(".feedback-bar__send").ClickAsync();

        await Assertions.Expect(tab.Locator(".feedback-bar__status")).ToHaveTextAsync("Thank you, the report was sent.");
        // The status text and the replacement land in one render, so no waiting is needed here.
        Assert.False(await tab.Locator(".feedback-bar__report").EvaluateAsync<bool>("e => e.__old === true"), "The old form was not replaced.");
        Assert.False(await tab.Locator(".feedback-bar__report").EvaluateAsync<bool>("e => e.open"), "The new form is open.");
        Assert.Equal(0, await tab.Locator(".feedback-bar__reason input:checked").CountAsync());
        Assert.Equal("", await tab.Locator(".feedback-bar__comment").InputValueAsync());
        Assert.True(
            await tab.Locator(".feedback-bar__status").EvaluateAsync<bool>("e => e === document.activeElement"),
            "Focus did not move to the status text after the report was stored.");
        Assert.Equal(1, await ReportsAsync(telegramId));
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Practice_Anonymous_ShowsNoFeedbackControls()
    {
        await using IBrowserContext context = await host.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString(),
            Locale = "en-US",
        });

        IPage tab = await OpenAnsweredAsync(context);

        await tab.Locator(".practice__next").WaitForAsync();
        Assert.Equal(0, await tab.Locator(".feedback-bar").CountAsync());
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Review_SignedIn_ShowsFeedbackControlsAfterAnswering()
    {
        const long telegramId = 990_700_002L;
        await using IBrowserContext context = await host.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString(),
            Locale = "en-US",
        });
        await host.SignInAsync(context, new FakeTelegramIdentity(telegramId, "Feedback Reviewer", "feedback_reviewer"));
        await host.RecordAnswersAsync(telegramId, (SingleId, Grade.DidntKnow, TimeSpan.FromDays(2)));
        IPage tab = await host.NewPageAsync(context);
        await tab.GotoAsync("/en/dotnet/review");
        await tab.Locator(".recall__show").ClickAsync(new LocatorClickOptions { Timeout = (float)IslandTimeout.TotalMilliseconds });
        Assert.Equal(0, await tab.Locator(".feedback-bar").CountAsync());

        await tab.Locator(".opt--pick").First.ClickAsync();
        await tab.Locator(".review__answer").ClickAsync();

        await tab.Locator(".feedback-bar").WaitForAsync();
        await tab.Locator(".feedback-bar__rate").Nth(0).ClickAsync();
        await Assertions.Expect(tab.Locator(".feedback-bar__rate").Nth(0)).ToHaveAttributeAsync("aria-pressed", "true");
        Assert.Equal(1, await RatingsAsync(telegramId));
        host.AssertNoCspViolations(tab);
    }

    private static long _nextTelegramId = 990_700_100L;

    private async Task<IPage> OpenSignedInAnsweredAsync(int width, string culture)
    {
        IBrowserContext context = await host.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString(),
            Locale = culture == "ru" ? "ru-RU" : "en-US",
            ViewportSize = new ViewportSize { Width = width, Height = 900 },
        });
        long telegramId = Interlocked.Increment(ref _nextTelegramId);
        await host.SignInAsync(context, new FakeTelegramIdentity(telegramId, "Feedback Layout", "feedback_layout"));
        IPage tab = await host.NewPageAsync(context);
        await tab.GotoAsync($"/{culture}/dotnet/practice?first={SingleId}");
        await tab.Locator(".qcard").WaitForAsync(new LocatorWaitForOptions { Timeout = (float)IslandTimeout.TotalMilliseconds });
        await tab.Locator(".opt--pick").First.ClickAsync();
        await tab.Locator(".practice__answer").ClickAsync();
        await tab.Locator(".feedback-bar").WaitForAsync();
        return tab;
    }

    [Fact]
    public async Task Practice_KeyInsideTheReportForm_DoesNotTriggerQuestionShortcuts()
    {
        IPage tab = await OpenSignedInAnsweredAsync(1280, "en");
        string title = await tab.Locator(".qcard__title").InnerTextAsync();
        await tab.Locator(".feedback-bar__summary").ClickAsync();
        await tab.Locator(".feedback-bar__reason input").First.FocusAsync();

        await tab.Keyboard.PressAsync("n");
        await tab.Keyboard.PressAsync("Enter");
        await tab.Keyboard.PressAsync("1");
        await tab.WaitForTimeoutAsync(750);

        Assert.Equal(title, await tab.Locator(".qcard__title").InnerTextAsync());
        Assert.Equal(1, await tab.Locator(".feedback-bar").CountAsync());
        Assert.True(await tab.Locator(".feedback-bar__report").EvaluateAsync<bool>("e => e.open"));

        // Outside the form the same key still goes to the next question.
        await tab.Locator(".practice__next").FocusAsync();
        await tab.Locator(".feedback-bar__rate").First.FocusAsync();
        await tab.Keyboard.PressAsync("n");
        await Assertions.Expect(tab.Locator(".qcard__title")).Not.ToHaveTextAsync(title);
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Practice_RatingButtons_KeepFocusWhileSaving()
    {
        IPage tab = await OpenSignedInAnsweredAsync(1280, "en");
        ILocator like = tab.Locator(".feedback-bar__rate").First;
        await like.FocusAsync();

        await tab.Keyboard.PressAsync("Enter");
        await Assertions.Expect(tab.Locator(".feedback-bar__status")).ToHaveTextAsync("Saved");

        Assert.True(await like.EvaluateAsync<bool>("e => e === document.activeElement"), "Focus left the rating button.");
        Assert.Equal(0, await tab.Locator(".feedback-bar__rate:disabled").CountAsync());
        await tab.Keyboard.PressAsync("Enter");
        await Assertions.Expect(like).ToHaveAttributeAsync("aria-pressed", "false");
        Assert.True(await like.EvaluateAsync<bool>("e => e === document.activeElement"));
    }

    public static TheoryData<int, string> Layouts()
    {
        TheoryData<int, string> data = [];
        foreach (int width in new[] { 375, 768, 1280, 1440 })
        {
            data.Add(width, "en");
            data.Add(width, "ru");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task Practice_SignedIn_FeedbackBarFitsWithTheFormOpen(int width, string culture)
    {
        IPage tab = await OpenSignedInAnsweredAsync(width, culture);
        await tab.Locator(".feedback-bar__rate").First.ClickAsync();
        await Assertions.Expect(tab.Locator(".feedback-bar__status")).Not.ToBeEmptyAsync();
        await tab.Locator(".feedback-bar__summary").ClickAsync();
        await tab.Locator(".feedback-bar__reason").Nth(2).ClickAsync();
        await tab.Locator(".feedback-bar__comment").FillAsync(new string('w', 400));
        await tab.EvaluateAsync("document.fonts.ready");

        string[] problems = await tab.EvaluateAsync<string[]>(
            """
            () => {
              const out = [];
              const w = window.innerWidth;
              if (document.documentElement.scrollWidth > w) out.push(`scrollWidth ${document.documentElement.scrollWidth} > ${w}`);
              const bar = document.querySelector('.feedback-bar');
              for (const e of bar.querySelectorAll('*')) {
                const r = e.getBoundingClientRect();
                if (r.width === 0 && r.height === 0) continue;
                if (r.left < -0.5 || r.right > w + 0.5) out.push(`${e.tagName.toLowerCase()}.${e.className} spills out: ${Math.round(r.left)}..${Math.round(r.right)} of ${w}`);
              }
              const targets = bar.querySelectorAll('.feedback-bar__rate, .feedback-bar__summary, .feedback-bar__reason, .feedback-bar__comment, .feedback-bar__send');
              for (const e of targets) {
                const r = e.getBoundingClientRect();
                if (r.width < 44 || r.height < 44) out.push(`${e.className} is ${Math.round(r.width)}x${Math.round(r.height)}`);
              }
              if (w >= 1440) {
                const feedback = document.querySelector('.feedback').getBoundingClientRect();
                const b = bar.getBoundingClientRect();
                if (Math.abs(b.left - feedback.left) > 1) out.push(`bar left ${b.left} is not the feedback's left ${feedback.left}`);
                const gap = b.top - feedback.bottom;
                if (gap < 0 || gap > 24) out.push(`bar is ${Math.round(gap)} px from the feedback's bottom`);
              }
              return out;
            }
            """);

        Assert.True(problems.Length == 0, $"{width} px ({culture}): {string.Join("; ", problems)}.");
        host.AssertNoCspViolations(tab);
    }

    private async Task<(IPage Tab, ResponseTap Reports)> OpenReportFormAsync()
    {
        IPage tab = await OpenSignedInAnsweredAsync(1280, "en");
        ResponseTap reports = await ResponseTap.AttachAsync(tab, "**/api/questions/*/reports");
        await tab.Locator(".feedback-bar__summary").ClickAsync();
        await tab.Locator(".feedback-bar__reason").Nth(0).ClickAsync();
        await tab.Locator(".feedback-bar__comment").FillAsync("Sent once.");
        return (tab, reports);
    }

    [Fact]
    public async Task Practice_DoubleClickOnSend_SendsOneReport()
    {
        (IPage tab, ResponseTap reports) = await OpenReportFormAsync();

        await tab.Locator(".feedback-bar__send").DblClickAsync();

        await Assertions.Expect(tab.Locator(".feedback-bar__status")).ToHaveTextAsync("Thank you, the report was sent.");
        await tab.WaitForLoadStateAsync(LoadState.NetworkIdle);
        Assert.Equal(1, reports.Count);
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task Practice_RepeatedEnterInTheForm_SendsOneReport()
    {
        (IPage tab, ResponseTap reports) = await OpenReportFormAsync();
        await tab.Locator(".feedback-bar__reason input").Nth(0).FocusAsync();

        for (int i = 0; i < 3; i++)
        {
            await tab.Keyboard.PressAsync("Enter");
        }

        await Assertions.Expect(tab.Locator(".feedback-bar__status")).ToHaveTextAsync("Thank you, the report was sent.");
        await tab.WaitForLoadStateAsync(LoadState.NetworkIdle);
        Assert.Equal(1, reports.Count);
        host.AssertNoCspViolations(tab);
    }
}
