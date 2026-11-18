using System.Text.RegularExpressions;

namespace Trainer.Web.Analytics;

/// <summary>
/// The static edition's GoatCounter settings (ADR 0025). The counting script itself is a self-hosted, reduced copy
/// (<c>wwwroot/js/goatcounter.js</c>), so no third-party script origin is allowed; only the beacon goes to the site's own
/// <c>https://&lt;code&gt;.goatcounter.com</c>.
/// </summary>
public static partial class GoatCounterScript
{
    [GeneratedRegex("^[a-z0-9-]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    /// <summary>Whether <paramref name="code"/> can be a GoatCounter site code, which is also a DNS label.</summary>
    public static bool IsValidCode(string? code) => code is not null && CodePattern().IsMatch(code);

    /// <summary>The host that receives the page-view beacons of a site code.</summary>
    public static string Host(string code) => $"https://{code}.goatcounter.com";

    /// <summary>The counting endpoint of a site code.</summary>
    public static string Endpoint(string code) => Host(code) + "/count";
}
