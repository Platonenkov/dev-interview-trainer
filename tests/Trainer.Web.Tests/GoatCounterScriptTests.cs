using System.Text.Json;
using Trainer.TestSupport;

namespace Trainer.Web.Tests;

/// <summary>
/// The self-hosted GoatCounter script (ADR 0025) run under Node against a fake browser: what a visitor typed or opened
/// must not appear in the beacon.
/// </summary>
public sealed class GoatCounterScriptTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Harness = """
        const fs = require('fs');
        const vm = require('vm');
        const [counterPath, settingsPath] = process.argv.slice(2);

        function run(scenario) {
          const beacons = [];
          const location = {
            origin: 'https://example.test', hostname: 'example.test', protocol: 'https:',
            pathname: scenario.pathname, search: scenario.search || '', hash: scenario.hash || '',
          };
          const tag = { dataset: { goatcounter: 'https://dit-test.goatcounter.com/count' } };
          const sandbox = {
            location, parent: { location }, console, URL, encodeURIComponent, Math, String,
            navigator: { doNotTrack: scenario.dnt, sendBeacon: (url) => { beacons.push(url); return true; }, webdriver: false },
            document: {
              referrer: scenario.referrer || '', title: scenario.title || 'A question', body: {}, visibilityState: 'visible',
              querySelector: (selector) => (selector === 'script[data-goatcounter]' ? tag : null),
              createElement: () => ({ style: {}, setAttribute() {}, addEventListener() {} }),
              addEventListener() {}, removeEventListener() {},
            },
            screen: { width: 1280, height: 720 }, devicePixelRatio: 2,
          };
          sandbox.window = sandbox;
          if (scenario.useSettings) {
            vm.runInNewContext(fs.readFileSync(settingsPath, 'utf8'), sandbox);
          }
          if (scenario.path) {
            sandbox.goatcounter = { path: () => scenario.path };
          }
          vm.runInNewContext(fs.readFileSync(counterPath, 'utf8'), sandbox);
          return beacons.map((url) => {
            const parsed = new URL(url);
            const params = {};
            parsed.searchParams.forEach((value, key) => { params[key] = value; });
            return { url, base: parsed.origin + parsed.pathname, params };
          });
        }

        const scenarios = JSON.parse(process.argv[4]);
        process.stdout.write(JSON.stringify(scenarios.map(run)));
        """;

    private static async Task<JsonElement[]> RunAsync(object[] scenarios)
    {
        using JsonDocument result = await NodeHarness.RunAsync(
            Harness,
            Ct,
            NodeHarness.WwwRootFile("js", "goatcounter.js"),
            NodeHarness.WwwRootFile("js", "goatcounter-settings.js"),
            JsonSerializer.Serialize(scenarios));
        return [.. result.RootElement.EnumerateArray().Select(e => e.Clone())];
    }

    private static JsonElement Single(JsonElement beacons)
    {
        Assert.Equal(1, beacons.GetArrayLength());
        return beacons[0];
    }

    [Fact]
    public async Task SearchPage_SendsNoQueryNoHashAndNoSameOriginReferrer()
    {
        JsonElement[] results = await RunAsync(
        [
            new
            {
                pathname = "/dit/en/dotnet/questions/",
                search = "?q=secret+term&level=junior",
                hash = "#frag",
                referrer = "https://example.test/dit/en/dotnet/questions/?q=secret+term",
                useSettings = true,
            },
        ]);

        JsonElement beacon = Single(results[0]);
        Assert.Equal("https://dit-test.goatcounter.com/count", beacon.GetProperty("base").GetString());
        JsonElement parameters = beacon.GetProperty("params");
        Assert.Equal("/dit/en/dotnet/questions/", parameters.GetProperty("p").GetString());
        Assert.False(parameters.TryGetProperty("q", out _), "the query string field must not be sent");
        Assert.False(parameters.TryGetProperty("r", out _), "a same-origin referrer must not be sent");
        string url = beacon.GetProperty("url").GetString()!;
        Assert.DoesNotContain("secret", url, StringComparison.Ordinal);
        Assert.DoesNotContain("level", url, StringComparison.Ordinal);
        Assert.DoesNotContain("frag", url, StringComparison.Ordinal);
        Assert.Equal(["b", "p", "rnd", "s", "t"], parameters.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ExternalReferrer_IsReducedToItsOrigin()
    {
        JsonElement[] results = await RunAsync(
        [
            new { pathname = "/dit/en/", referrer = "https://news.example.org/some/path?x=secret#h", useSettings = true },
            new { pathname = "/dit/en/", referrer = "not a url", useSettings = true },
            new { pathname = "/dit/en/", referrer = "android-app://com.example/path", useSettings = true },
        ]);

        Assert.Equal("https://news.example.org", Single(results[0]).GetProperty("params").GetProperty("r").GetString());
        Assert.False(Single(results[1]).GetProperty("params").TryGetProperty("r", out _));
        Assert.False(Single(results[2]).GetProperty("params").TryGetProperty("r", out _));
        Assert.DoesNotContain("secret", Single(results[0]).GetProperty("url").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PathCallbackWithQueryOrHash_IsCutToThePath()
    {
        JsonElement[] results = await RunAsync(
        [
            new { pathname = "/dit/en/", path = "/dit/en/x/?q=secret#h" },
            new { pathname = "/dit/en/dotnet/", search = "?q=secret" },
            new { pathname = "/dit/en/", path = "?q=secret" },
        ]);

        Assert.Equal("/dit/en/x/", Single(results[0]).GetProperty("params").GetProperty("p").GetString());
        Assert.Equal("/dit/en/dotnet/", Single(results[1]).GetProperty("params").GetProperty("p").GetString());
        Assert.Equal("/dit/en/", Single(results[2]).GetProperty("params").GetProperty("p").GetString());
        Assert.All(results, r => Assert.DoesNotContain("secret", Single(r).GetProperty("url").GetString(), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("1", 0)]
    [InlineData("0", 1)]
    [InlineData(null, 1)]
    public async Task DoNotTrack_IsHonoured(string? doNotTrack, int expectedBeacons)
    {
        JsonElement[] results = await RunAsync([new { pathname = "/dit/en/", dnt = doNotTrack, useSettings = true }]);

        Assert.Equal(expectedBeacons, results[0].GetArrayLength());
    }

    [Fact]
    public void CounterSource_KeepsTheLicenceHeaderAndNeverReadsTheQueryString()
    {
        string source = File.ReadAllText(NodeHarness.WwwRootFile("js", "goatcounter.js"));

        Assert.Contains("released under the ISC license", source, StringComparison.Ordinal);
        Assert.Contains("Changes against the upstream file", source, StringComparison.Ordinal);
        Assert.DoesNotContain("location.search", source, StringComparison.Ordinal);
        Assert.DoesNotContain("location.hash", source, StringComparison.Ordinal);
        Assert.DoesNotContain("q:", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void MissingNode_FailsOnCiAndSkipsLocally(string? ci, bool fails)
    {
        Assert.Equal(fails, NodeHarness.MissingNodeIsFailure(ci));
    }
}
