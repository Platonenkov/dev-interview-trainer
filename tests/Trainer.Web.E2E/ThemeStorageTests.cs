using Microsoft.Playwright;
using Trainer.Web.Startup;

namespace Trainer.Web.E2E;

/// <summary>
/// Runs the inline theme scripts in a browser. The old bare <c>theme</c> key is adopted by the server edition only: on the
/// shared github.io origin that key belongs to other sites. Without a stored choice the light colour scheme gives "light",
/// so "dark" can only come from an adopted key.
/// </summary>
[Trait("Category", "E2E")]
public sealed class ThemeStorageTests(AppHostFixture host) : IClassFixture<AppHostFixture>
{
    private async Task<(string Theme, string? Namespaced, string? Legacy)> RunAsync(string script, string? legacy, string? namespaced)
    {
        // BypassCSP: the page's policy allows only the real inline script's hash, and this runs the same text separately.
        await using IBrowserContext context = await host.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress.ToString(),
            ColorScheme = ColorScheme.Light,
            BypassCSP = true,
        });
        IPage tab = await host.NewPageAsync(context);
        await tab.GotoAsync("/en/about");
        await tab.EvaluateAsync(
            "([l, n]) => { localStorage.clear(); if (l) localStorage.setItem('theme', l); if (n) localStorage.setItem('dit.theme', n); document.documentElement.removeAttribute('data-theme'); }",
            new string?[] { legacy, namespaced });
        await tab.AddScriptTagAsync(new PageAddScriptTagOptions { Content = script });
        return (
            await tab.EvaluateAsync<string>("() => document.documentElement.dataset.theme"),
            await tab.EvaluateAsync<string?>("() => localStorage.getItem('dit.theme')"),
            await tab.EvaluateAsync<string?>("() => localStorage.getItem('theme')"));
    }

    [Fact]
    public async Task Server_OnlyLegacyKey_IsAdoptedAndCopied()
    {
        (string theme, string? namespaced, string? legacy) = await RunAsync(ThemeScript.For(SiteEdition.Server), "dark", null);

        Assert.Equal("dark", theme);
        Assert.Equal("dark", namespaced);
        Assert.Equal("dark", legacy);
    }

    [Fact]
    public async Task Static_OnlyLegacyKey_IsIgnoredAndNothingAdopted()
    {
        (string theme, string? namespaced, string? legacy) = await RunAsync(ThemeScript.For(SiteEdition.Static), "dark", null);

        Assert.Equal("light", theme);
        Assert.Null(namespaced);
        Assert.Equal("dark", legacy);
    }

    [Theory]
    [InlineData(SiteEdition.Server)]
    [InlineData(SiteEdition.Static)]
    public async Task NamespacedKey_WinsOverTheLegacyKey(SiteEdition edition)
    {
        (string theme, string? namespaced, _) = await RunAsync(ThemeScript.For(edition), "dark", "light");

        Assert.Equal("light", theme);
        Assert.Equal("light", namespaced);
    }
}
