using Microsoft.Playwright;

namespace Trainer.Web.E2E;

/// <summary>
/// One Playwright driver and one headless Chromium for the whole run; every test isolates itself in its own browser
/// context. A browser per test class meant a dozen drivers and browsers starting at once next to a dozen hosts, and at
/// that peak the machine is CPU-bound for minutes: navigations then waited past their 30 s timeout before the first
/// response byte arrived (the timeouts were not specific to any page or test).
/// </summary>
public sealed class BrowserFixture : IAsyncLifetime
{
    private IPlaywright? _playwright;

    public IBrowser Browser { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    public async ValueTask DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.CloseAsync();
        }

        _playwright?.Dispose();
    }
}
