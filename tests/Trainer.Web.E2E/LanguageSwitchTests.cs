using Microsoft.Playwright;

namespace Trainer.Web.E2E;

[Trait("Category", "E2E")]
public sealed class LanguageSwitchTests(AppHostFixture host) : IClassFixture<AppHostFixture>
{
    [Fact]
    public async Task Switcher_LandsOnTheSamePageInTheOtherCulture_AndRemembersTheChoice()
    {
        await using IBrowserContext context = await host.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString(),
            Locale = "en-US",
        });
        IPage tab = await context.NewPageAsync();
        await tab.GotoAsync("/en/dotnet/questions?level=middle");

        await tab.Locator(".lang-switch a[hreflang='ru']").ClickAsync();
        await tab.WaitForURLAsync("**/ru/dotnet/questions?level=middle");

        Assert.Equal("ru", await tab.EvaluateAsync<string>("document.documentElement.lang"));
        IReadOnlyList<BrowserContextCookiesResult> cookies = await context.CookiesAsync();
        Assert.Equal("ru", Assert.Single(cookies, c => c.Name == "lang").Value);

        await tab.GotoAsync("/");
        Assert.EndsWith("/ru", new Uri(tab.Url).AbsolutePath, StringComparison.Ordinal);
    }
}
