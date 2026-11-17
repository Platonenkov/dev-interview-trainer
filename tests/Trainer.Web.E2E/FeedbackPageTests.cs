using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Trainer.Infrastructure.Persistence;
using Trainer.TestSupport;

namespace Trainer.Web.E2E;

/// <summary>
/// Rating and reporting from the static question page in a real browser (ADR 0016): plain POST forms that work without
/// JavaScript, are usable on a phone, and answer with a redirect back to the feedback section.
/// </summary>
[Trait("Category", "E2E")]
public sealed class FeedbackPageTests(AppHostFixture host) : IClassFixture<AppHostFixture>
{
    private const string QuestionPath = "/en/dotnet/questions/closure-for-loop";
    private const int MinimumTarget = 44;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<int> CountAsync(FormattableString sql)
    {
        await using TrainerDbContext db = PostgresFixture.CreateContext(host.ConnectionString);
        return await db.Database.SqlQuery<int>(sql).SingleAsync(Ct);
    }

    private Task<int> RatingsAsync(long telegramId) => CountAsync(
        $"SELECT count(*)::int AS \"Value\" FROM question_ratings r JOIN users u ON u.id = r.user_id WHERE u.telegram_user_id = {telegramId}");

    private Task<int> ReportsAsync(long telegramId) => CountAsync(
        $"SELECT count(*)::int AS \"Value\" FROM question_reports r JOIN users u ON u.id = r.user_id WHERE u.telegram_user_id = {telegramId}");

    private static async Task<string[]> UndersizedAsync(IPage tab) =>
        await tab.EvaluateAsync<string[]>(
            """
            (min) => [...document.querySelectorAll('#feedback button, #feedback summary, #feedback textarea, #feedback label:has(> input)')]
              .filter(e => e.getClientRects().length > 0)
              .map(e => ({ e, r: e.getBoundingClientRect() }))
              .filter(x => x.r.width < min || x.r.height < min)
              .map(x => `${x.e.tagName.toLowerCase()}.${x.e.className} "${(x.e.textContent || '').trim().slice(0, 30)}" ${Math.round(x.r.width)}x${Math.round(x.r.height)}`)
            """,
            MinimumTarget);

    [Fact]
    public async Task FeedbackPage_SignedIn_ReportFormUsableAt375px()
    {
        const long telegramId = 990_720_001L;
        await using IBrowserContext context = await host.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString(),
            Locale = "en-US",
            ViewportSize = new ViewportSize { Width = 375, Height = 812 },
        });
        await host.SignInAsync(context, new FakeTelegramIdentity(telegramId, "Phone Reader", "phone_reader"));
        IPage tab = await host.NewPageAsync(context);
        await tab.GotoAsync(QuestionPath);
        await Assertions.Expect(tab.Locator("#feedback")).ToBeVisibleAsync();

        Assert.True(await tab.EvaluateAsync<int>("document.documentElement.scrollWidth") <= 375, "The page scrolls sideways before the form opens.");
        await tab.Locator(".feedback-bar__summary").ClickAsync();
        await Assertions.Expect(tab.Locator(".feedback-bar__reasons")).ToBeVisibleAsync();
        await tab.EvaluateAsync("document.fonts.ready");

        Assert.True(await tab.EvaluateAsync<int>("document.documentElement.scrollWidth") <= 375, "The open report form makes the page scroll sideways.");
        string[] undersized = await UndersizedAsync(tab);
        Assert.True(undersized.Length == 0, $"Feedback targets below {MinimumTarget} px: {string.Join("; ", undersized)}.");
        Assert.True(await tab.Locator("#feedback label:has(> input)").CountAsync() >= 5, "The five reasons are not all rendered.");

        // The send button is native-required: without a reason the browser blocks the submit and nothing is stored.
        await tab.Locator(".feedback-bar__send").ClickAsync();
        Assert.DoesNotMatch("feedback=", tab.Url);

        await tab.Locator(".feedback-bar__reason").Nth(1).ClickAsync();
        await tab.Locator(".feedback-bar__comment").FillAsync("The wording of option B is ambiguous.\nSecond line.");
        await tab.Locator(".feedback-bar__send").ClickAsync();

        await tab.WaitForURLAsync(new Regex(@"closure-for-loop\?feedback=reported#feedback$"));
        await Assertions.Expect(tab.Locator(".feedback-forms__notice")).ToHaveTextAsync("Thank you, the report was sent.");
        Assert.Equal("feedback", await tab.EvaluateAsync<string>("document.querySelector(':target')?.id ?? ''"));
        Assert.True(await tab.EvaluateAsync<int>("document.documentElement.scrollWidth") <= 375, "The page scrolls sideways after the redirect.");
        Assert.Equal(1, await ReportsAsync(telegramId));
        host.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task FeedbackPage_SignedIn_RatesAndClearsWithoutJavaScript()
    {
        const long telegramId = 990_720_002L;
        // The sign-in flow runs in a context with JavaScript; only the session cookie is copied to the context without it.
        await using IBrowserContext noScript = await host.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString(),
            Locale = "en-US",
            JavaScriptEnabled = false,
        });
        await using IBrowserContext signedIn = await host.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString(),
            Locale = "en-US",
        });
        await host.SignInAsync(signedIn, new FakeTelegramIdentity(telegramId, "Plain Reader", "plain_reader"));
        await noScript.AddCookiesAsync(
            (await signedIn.CookiesAsync()).Select(c => new Cookie { Name = c.Name, Value = c.Value, Url = host.BaseAddress.ToString() }));

        IPage tab = await noScript.NewPageAsync();
        await tab.GotoAsync(QuestionPath);
        ILocator like = tab.Locator("button[name=value][value=like]");
        ILocator dislike = tab.Locator("button[name=value][value=dislike]");
        await Assertions.Expect(like).ToHaveAttributeAsync("aria-pressed", "false");

        await like.ClickAsync();
        await tab.WaitForURLAsync(new Regex(@"\?feedback=rated#feedback$"));
        await Assertions.Expect(tab.Locator(".feedback-forms__notice")).ToHaveTextAsync("Thank you, your rating is saved.");
        await Assertions.Expect(like).ToHaveAttributeAsync("aria-pressed", "true");
        Assert.Equal(1, await RatingsAsync(telegramId));

        await dislike.ClickAsync();
        await Assertions.Expect(dislike).ToHaveAttributeAsync("aria-pressed", "true");
        await Assertions.Expect(like).ToHaveAttributeAsync("aria-pressed", "false");
        Assert.Equal(1, await RatingsAsync(telegramId));

        await tab.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Remove my rating" }).ClickAsync();
        await tab.WaitForURLAsync(new Regex(@"\?feedback=cleared#feedback$"));
        await Assertions.Expect(tab.Locator(".feedback-forms__notice")).ToHaveTextAsync("Your rating was removed.");
        await Assertions.Expect(dislike).ToHaveAttributeAsync("aria-pressed", "false");
        Assert.Equal(0, await RatingsAsync(telegramId));
    }
}
