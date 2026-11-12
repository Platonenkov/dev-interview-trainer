namespace Trainer.Snippets;

/// <summary>Limits and image for the snippet containers. The image is pinned by digest so that a moving tag cannot change what content is verified against.</summary>
public sealed record SandboxOptions
{
    public string Image { get; init; } =
        "mcr.microsoft.com/dotnet/sdk@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb";

    public TimeSpan BuildDeadline { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan RunDeadline { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Budget of each container cleanup command (kill, rm); independent of the snippet deadline and of the caller's token.</summary>
    public TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public int BuildMemoryMb { get; init; } = 2048;
    public int RunMemoryMb { get; init; } = 256;
    public int OutputCapBytes { get; init; } = 65536;
}
