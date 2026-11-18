using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Trainer.Application.Users;
using Trainer.Domain.Feedback;
using Trainer.TestSupport;
using Trainer.Web.Auth;
using Trainer.Web.Caching;

namespace Trainer.Web.E2E;

/// <summary>
/// The admin page in a real browser: layout at the supported widths, 44 px targets, and a block action through the
/// form (the in-memory tests post fields by hand; this proves the rendered form works in a browser).
/// </summary>
[Trait("Category", "E2E")]
public sealed class AdminPageTests(AppHostFixture host) : IClassFixture<AppHostFixture>, IAsyncLifetime
{
    private const int SeededUsers = 55;
    private const int MinimumTarget = 44;
    private const string ClosureQuestionId = "01KBZ3Q8N9V2K3M4P5R6S7T8WA";

    private static readonly string ScreenshotDirectory = Path.Combine(ContentWorkspace.RepositoryRoot(), "TestResults", "screenshots");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        IUserAccountService users = scope.ServiceProvider.GetRequiredService<IUserAccountService>();
        for (int i = 0; i < SeededUsers; i++)
        {
            string name = i % 3 == 0 ? "A very long display name that has to wrap inside its cell without pushing the page sideways " + i : "Reader " + i;
            await users.SignInAsync(new TelegramProfile(991_000_000L + i, "reader_with_a_long_username_" + i, name), Ct);
        }

        // Report rows with the comment shapes most likely to push the table sideways: many lines and one unbreakable word.
        await host.SeedFeedbackAsync(
            991_100_001L, "A reporter with a very long display name that has to wrap inside its cell", ClosureQuestionId, "ru", ReportReason.WrongAnswer,
            "First line\n" + new string('w', 150) + "\nLast line", dislike: true);
        await host.SeedFeedbackAsync(991_100_002L, "Short", ClosureQuestionId, "en", ReportReason.Other, null, dislike: false);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<IBrowserContext> NewAdminContextAsync(int width, string theme)
    {
        IBrowserContext context = await host.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = width, Height = 900 },
            BaseURL = host.BaseAddress.ToString(),
        });
        await context.AddInitScriptAsync($"try {{ localStorage.setItem('dit.theme', '{theme}'); }} catch (e) {{ }}");
        await context.AddCookiesAsync([new Cookie { Name = AuthCookieNames.Session, Value = await host.IssueAdminSessionAsync(), Url = host.BaseAddress.ToString() }]);
        return context;
    }

    public static TheoryData<int, string> Layouts() => new()
    {
        { 320, "dark" },
        { 375, "dark" },
        { 375, "light" },
        { 768, "dark" },
        { 1280, "dark" },
        { 1280, "light" },
        { 1920, "dark" },
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task AdminPage_FitsTheViewportAndHasLargeTargets(int width, string theme)
    {
        await using IBrowserContext context = await NewAdminContextAsync(width, theme);
        IPage tab = await context.NewPageAsync();
        IResponse? response = await tab.GotoAsync("/en/admin");
        Assert.Equal(200, response?.Status);
        await tab.EvaluateAsync("document.fonts.ready");

        int scrollWidth = await tab.EvaluateAsync<int>("document.documentElement.scrollWidth");
        int innerWidth = await tab.EvaluateAsync<int>("window.innerWidth");
        string[] undersized = await tab.EvaluateAsync<string[]>(
            """
            (min) => [...document.querySelectorAll('.admin a[href], .admin button, .admin input:not([type=hidden])')]
              .filter(e => e.getClientRects().length > 0)
              .map(e => ({ e, r: e.getBoundingClientRect() }))
              .filter(x => x.r.width < min || x.r.height < min)
              .map(x => `${x.e.tagName.toLowerCase()} "${(x.e.textContent || x.e.name || '').trim().slice(0, 30)}" ${Math.round(x.r.width)}x${Math.round(x.r.height)}`)
            """,
            MinimumTarget);

        Directory.CreateDirectory(ScreenshotDirectory);
        await tab.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = Path.Combine(ScreenshotDirectory, $"admin-{width}-{theme}.png"),
            FullPage = true,
        });

        string wide = await tab.EvaluateAsync<string>("[...document.querySelectorAll('*')].filter(e => !e.closest('.table-scroll') && e.getBoundingClientRect().right > window.innerWidth + 1).slice(0, 6).map(e => e.tagName + '.' + e.className).join(' | ')");
        Assert.True(scrollWidth <= innerWidth, $"{wide} admin at {width} px ({theme}): scrollWidth {scrollWidth} > innerWidth {innerWidth}.");
        Assert.True(undersized.Length == 0, $"admin at {width} px ({theme}) has targets below {MinimumTarget} px: {string.Join("; ", undersized)}.");
    }

    [Fact]
    public async Task AdminPage_ResolveButton_ClosesTheReportThroughTheForm()
    {
        await host.SeedFeedbackAsync(991_100_003L, "Resolver Subject", ClosureQuestionId, "en", ReportReason.Outdated, "Resolve me through the form", dislike: false);
        await using IBrowserContext context = await NewAdminContextAsync(1280, "dark");
        IPage tab = await context.NewPageAsync();
        await tab.GotoAsync("/en/admin");

        ILocator row = tab.Locator(".admin-table--reports tr", new PageLocatorOptions { HasText = "Resolve me through the form" });
        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Resolve" }).ClickAsync();

        await Assertions.Expect(tab.GetByRole(AriaRole.Status)).ToContainTextAsync("Report resolved.");
        await Assertions.Expect(tab.Locator(".admin-table--reports tr", new PageLocatorOptions { HasText = "Resolve me through the form" })).ToHaveCountAsync(0);

        await tab.GotoAsync("/en/admin?reports=resolved");
        await Assertions.Expect(tab.Locator(".admin-table--reports tr", new PageLocatorOptions { HasText = "Resolve me through the form" })).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task AdminPage_BlockButton_BlocksTheUserThroughTheForm()
    {
        await using IBrowserContext context = await NewAdminContextAsync(1280, "dark");
        IPage tab = await context.NewPageAsync();
        await tab.GotoAsync("/en/admin?q=991000007");

        await tab.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Block" }).ClickAsync();

        await Assertions.Expect(tab.GetByRole(AriaRole.Status)).ToContainTextAsync("User blocked");
        await Assertions.Expect(tab.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Unblock" })).ToBeVisibleAsync();
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        UserAccess? access = await scope.ServiceProvider.GetRequiredService<IUserAccountService>().GetAccessAsync(991_000_007L, Ct);
        Assert.True(access?.IsBlocked);
    }
}
