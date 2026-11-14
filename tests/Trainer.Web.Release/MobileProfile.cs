using Microsoft.Playwright;

namespace Trainer.Web.Release;

/// <summary>
/// The fixed stand-in for "a mid-range phone over 4G" (spec 8.2): 150 ms latency, 9 Mbit/s down, 1.5 Mbit/s up, the CPU
/// four times slower, a 375×812 touch viewport. Applied per tab through the Chrome DevTools Protocol.
/// </summary>
public static class MobileProfile
{
    public const int Width = 375;
    public const int Height = 812;
    public const int LatencyMs = 150;
    public const double DownloadMbps = 9;
    public const double UploadMbps = 1.5;
    public const int CpuSlowdown = 4;

    public static async Task ApplyAsync(ICDPSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        await session.SendAsync("Network.emulateNetworkConditions", new Dictionary<string, object>
        {
            ["offline"] = false,
            ["latency"] = LatencyMs,
            ["downloadThroughput"] = DownloadMbps * 1_000_000 / 8,
            ["uploadThroughput"] = UploadMbps * 1_000_000 / 8,
        });
        await session.SendAsync("Emulation.setCPUThrottlingRate", new Dictionary<string, object> { ["rate"] = CpuSlowdown });
    }
}
