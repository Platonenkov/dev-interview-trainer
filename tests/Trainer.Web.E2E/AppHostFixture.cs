using Microsoft.Playwright;
using Trainer.TestSupport;

namespace Trainer.Web.E2E;

/// <summary>
/// The real site on a Kestrel port (a browser cannot reach the in-memory test server) plus one headless Chromium.
/// Run <c>playwright.ps1 install chromium</c> from the build output once per machine before the first run.
/// </summary>
public sealed class AppHostFixture(PostgresFixture postgres) : IAsyncLifetime
{
    private TrainerWebFactory? _factory;
    private IPlaywright? _playwright;

    public IBrowser Browser { get; private set; } = null!;

    public Uri BaseAddress { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await TrainerWebFactory.CreateAsync(postgres);
        _factory.UseKestrel(0);
        _factory.StartServer();
        BaseAddress = _factory.ClientOptions.BaseAddress;
        await _factory.WaitUntilReadyAsync();

        try
        {
            _playwright = await Playwright.CreateAsync();
            Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }
        catch
        {
            // xUnit does not call DisposeAsync when InitializeAsync throws, so the running host would leak.
            await DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.CloseAsync();
        }

        _playwright?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }
}
