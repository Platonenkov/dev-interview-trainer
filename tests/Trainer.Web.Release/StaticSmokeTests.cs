using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Playwright;

namespace Trainer.Web.Release;

/// <summary>
/// The exported Release site, served the way GitHub Pages serves it, in a real browser: every kind of page loads without a
/// console error, Practice starts, and the page of the previous live build still boots from this artifact's retained files.
/// Runs in the StaticSmoke category after the export of a deployment and fails (not skips) when that export is missing and
/// <c>DIT_STATIC_SMOKE_REQUIRED=1</c>.
/// </summary>
[Trait("Category", "StaticSmoke")]
public sealed class StaticSmokeTests(StaticSiteFolder site)
{
    private const string ReadyIsland = ".island-root[data-island-ready='true']";
    private static readonly float IslandTimeout = 120_000;

    [Fact]
    public async Task Smoke_EveryPageKindLoadsAndPracticeStarts()
    {
        site.SkipUnlessAvailable();
        site.Reset();
        string topic = SitemapPath("/topics/", exclude: null);
        string question = SitemapPath("/questions/", exclude: "/questions/$");
        await using IBrowserContext context = await site.NewContextAsync();
        IPage tab = await site.NewPageAsync(context);

        (string Path, int Status, bool Island)[] pages =
        [
            ("en/", 200, false),
            ("en/dotnet/questions/", 200, false),
            (topic, 200, false),
            (question, 200, false),
            ("en/about/", 200, false),
            ("ru/", 200, false),
            ("en/dotnet/practice/", 200, true),
            ("en/dotnet/mock/", 200, true),
            ("en/dotnet/review/", 200, true),
            ("en/dotnet/profile/", 200, true),
        ];
        foreach ((string path, int status, bool island) in pages)
        {
            IResponse? response = await tab.GotoAsync(site.Url(path).ToString());
            Assert.True(response!.Status == status, $"{path} answered {response.Status}, expected {status}");
            if (island)
            {
                await tab.Locator(ReadyIsland).WaitForAsync(new LocatorWaitForOptions { Timeout = IslandTimeout });
            }

            Assert.Equal(0, await tab.Locator("#site-updated:not([hidden])").CountAsync());
            Assert.Equal(1, await tab.Locator("h1").CountAsync());
        }

        // The language chooser at the site root sends the browser to a language home.
        await tab.GotoAsync(site.BaseAddress.ToString());
        await tab.WaitForURLAsync(url => url.EndsWith("/en/", StringComparison.Ordinal));

        // An unknown path is the 404 page, whose own load is the one console error a browser reports for it.
        IResponse? missing = await tab.GotoAsync(site.Url("en/no-such-page-anywhere/").ToString());
        Assert.Equal(404, missing!.Status);
        await tab.Locator("h1#not-found").WaitForAsync();

        await tab.GotoAsync(site.Url("en/dotnet/practice/").ToString());
        await tab.Locator(ReadyIsland).WaitForAsync(new LocatorWaitForOptions { Timeout = IslandTimeout });
        await tab.Locator(".pf__start").ClickAsync();
        await tab.Locator(".qcard").WaitForAsync(new LocatorWaitForOptions { Timeout = IslandTimeout });

        string[] errors = site.ConsoleErrors(tab, text => text.Contains("status of 404", StringComparison.Ordinal));
        Assert.True(errors.Length == 0, "Console errors: " + string.Join(" | ", errors));
        site.AssertNoCspViolations(tab);
    }

    [Fact]
    public async Task PreviousLiveIslandPage_BootsFromThisArtifact()
    {
        site.SkipUnlessAvailable();
        // Only the first deployment has no previous page; every later one must not skip.
        Assert.SkipUnless(site.PreviousPageAddress is not null, $"{StaticSiteFolder.PreviousPageVariable} is not set: this is the first deployment.");
        site.Reset();
        string artifactBuild = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(site.Inputs!.Directory, "build.json"), TestContext.Current.CancellationToken))
            .RootElement.GetProperty("buildId").GetString()!;
        await using IBrowserContext context = await site.NewContextAsync();
        IPage tab = await site.NewPageAsync(context);
        List<string> requests = [];
        tab.Request += (_, request) =>
        {
            if (request.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                lock (requests)
                {
                    requests.Add(request.Url);
                }
            }
        };

        // The page of the build that was live before this artifact asks for the runtime, styles and content it names.
        await tab.GotoAsync(site.PreviousPageAddress!.ToString());
        await tab.Locator(ReadyIsland).WaitForAsync(new LocatorWaitForOptions { Timeout = IslandTimeout });

        string previousBuild = (await tab.Locator("meta[name='dit-build']").GetAttributeAsync("content"))!;
        Assert.NotEqual(artifactBuild, previousBuild);
        Assert.Equal(0, await tab.Locator("#site-updated:not([hidden])").CountAsync());
        Assert.Equal(0, await tab.Locator(".island-state").CountAsync());
        await tab.Locator(".pf__start").ClickAsync();
        await tab.Locator(".qcard").WaitForAsync(new LocatorWaitForOptions { Timeout = IslandTimeout });

        Assert.DoesNotContain(site.Served, request => request.Status >= 400 && !request.Path.EndsWith(".br", StringComparison.Ordinal));
        string[] strays;
        lock (requests)
        {
            strays = [.. requests.Where(url => !url.StartsWith(site.BaseAddress.GetLeftPart(UriPartial.Authority) + site.Prefix, StringComparison.Ordinal) && !new Uri(url).Host.EndsWith(".goatcounter.com", StringComparison.Ordinal))];
        }

        Assert.True(strays.Length == 0, "Requests outside the prefix: " + string.Join(", ", strays));
        string[] errors = site.ConsoleErrors(tab);
        Assert.True(errors.Length == 0, "Console errors: " + string.Join(" | ", errors));
        site.AssertNoCspViolations(tab);
    }

    [Fact]
    public void MissingInputs_FailWhenRequired()
    {
        InvalidOperationException noDirectory = Assert.Throws<InvalidOperationException>(
            () => new StaticSiteFolder(name => name == StaticSiteFolder.RequiredVariable ? "1" : null));
        Assert.Contains(StaticSiteFolder.DirectoryVariable, noDirectory.Message, StringComparison.Ordinal);

        string directory = Path.Combine(Path.GetTempPath(), "dit-static-inputs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            InvalidOperationException noUrl = Assert.Throws<InvalidOperationException>(() => new StaticSiteFolder(name => name switch
            {
                StaticSiteFolder.RequiredVariable => "1",
                StaticSiteFolder.DirectoryVariable => directory,
                _ => null,
            }));
            Assert.Contains(StaticSiteFolder.UrlVariable, noUrl.Message, StringComparison.Ordinal);

            // Not required: the same absence is a quiet skip, not a failure.
            Assert.False(new StaticSiteFolder(_ => null).IsAvailable);
            Assert.False(new StaticSiteFolder(name => name == StaticSiteFolder.DirectoryVariable ? directory : null).IsAvailable);

            StaticSiteFolder complete = new(name => name switch
            {
                StaticSiteFolder.DirectoryVariable => directory,
                StaticSiteFolder.UrlVariable => "https://owner.github.io/dit/",
                _ => null,
            });
            Assert.True(complete.IsAvailable);
            Assert.Null(complete.Inputs!.PreviousPage);

            // A previous page that is named but not there is a failed capture, not a first deployment: it fails with or without
            // the required flag.
            string missingPage = Path.Combine(directory, "no-such-page.html");
            foreach (string? required in new string?[] { null, "1" })
            {
                InvalidOperationException brokenCapture = Assert.Throws<InvalidOperationException>(() => new StaticSiteFolder(name => name switch
                {
                    StaticSiteFolder.RequiredVariable => required,
                    StaticSiteFolder.DirectoryVariable => directory,
                    StaticSiteFolder.UrlVariable => "https://owner.github.io/dit/",
                    StaticSiteFolder.PreviousPageVariable => missingPage,
                    _ => null,
                }));
                Assert.Contains(StaticSiteFolder.PreviousPageVariable, brokenCapture.Message, StringComparison.Ordinal);
                Assert.Contains(missingPage, brokenCapture.Message, StringComparison.Ordinal);
            }

            string page = Path.Combine(directory, "previous.html");
            File.WriteAllText(page, "<html></html>");
            Assert.Equal(
                page,
                new StaticSiteFolder(name => name switch
                {
                    StaticSiteFolder.DirectoryVariable => directory,
                    StaticSiteFolder.UrlVariable => "https://owner.github.io/dit/",
                    StaticSiteFolder.PreviousPageVariable => page,
                    _ => null,
                }).Inputs!.PreviousPage);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // The path (relative to the site root) of the first English sitemap entry that contains <paramref name="fragment"/>.
    private string SitemapPath(string fragment, string? exclude)
    {
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        XDocument document = XDocument.Load(Path.Combine(site.Inputs!.Directory, "sitemap-en.xml"));
        string pathBase = site.Prefix;
        foreach (string loc in document.Descendants(ns + "loc").Select(e => e.Value))
        {
            string path = Uri.UnescapeDataString(new Uri(loc).AbsolutePath);
            string relative = path[pathBase.Length..];
            bool excluded = exclude is not null && System.Text.RegularExpressions.Regex.IsMatch(path, exclude);
            if (relative.Contains(fragment.TrimStart('/'), StringComparison.Ordinal) && !excluded && path.StartsWith(pathBase, StringComparison.Ordinal))
            {
                return relative;
            }
        }

        throw new InvalidOperationException($"sitemap-en.xml lists no page with '{fragment}'.");
    }
}
