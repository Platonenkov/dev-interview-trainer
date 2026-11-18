using System.Diagnostics;
using Trainer.TestSupport;

namespace Trainer.Deploy.Tests;

public sealed class ShellScriptTests
{
    [Theory]
    [InlineData("render-env.test.sh")]
    [InlineData("lib.test.sh")]
    [InlineData("deploy-lib.test.sh")]
    [InlineData("backup-lib.test.sh")]
    [InlineData("pages-gate.test.sh")]
    public async Task ShellTestScript_Passes(string scriptName)
    {
        string? bash = FindBash();
        Assert.SkipWhen(bash is null, "bash is not installed.");

        string script = Path.Combine(ContentWorkspace.RepositoryRoot(), "deploy", "tests", scriptName);
        ProcessStartInfo info = new(bash!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = ContentWorkspace.RepositoryRoot(),
        };
        info.ArgumentList.Add(script);

        using Process process = Process.Start(info)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.True(process.ExitCode == 0, await stdout + await stderr);
    }

    // The script takes the deploy lock, which needs flock; Git Bash on Windows has none, CI and the Linux hosts do.
    [Fact]
    public async Task RotateTestScript_Passes()
    {
        string? bash = FindBash();
        Assert.SkipWhen(bash is null || !OperatingSystem.IsLinux(), "rotate.test.sh needs Linux (flock) and bash.");

        string script = Path.Combine(ContentWorkspace.RepositoryRoot(), "deploy", "tests", "rotate.test.sh");
        ProcessStartInfo info = new(bash!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = ContentWorkspace.RepositoryRoot(),
        };
        info.ArgumentList.Add(script);

        using Process process = Process.Start(info)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.True(process.ExitCode == 0, await stdout + await stderr);
    }

    [Theory]
    [InlineData("no such manifest: ghcr.io/example/dit:abc", 0)]
    [InlineData("Error response from daemon: manifest unknown", 0)]
    [InlineData("Error: image not found", 0)]
    [InlineData("denied: denied", 0)]
    [InlineData("Get \"https://ghcr.io/v2/\": dial tcp 140.82.0.1:443: i/o timeout", 1)]
    [InlineData("permission denied while trying to connect to the Docker daemon socket", 1)]
    [InlineData("", 1)]
    public async Task ManifestMissing_TreatsOnlyAnAbsentImageAsMissing(string inspectOutput, int expectedExitCode)
    {
        string? bash = FindBash();
        Assert.SkipWhen(bash is null, "bash is not installed.");

        ProcessStartInfo info = new(bash!)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = ContentWorkspace.RepositoryRoot(),
        };
        info.ArgumentList.Add(Path.Combine(ContentWorkspace.RepositoryRoot(), "deploy", "scripts", "manifest-missing.sh"));

        using Process process = Process.Start(info)!;
        await process.StandardInput.WriteAsync(inspectOutput.AsMemory(), TestContext.Current.CancellationToken);
        process.StandardInput.Close();
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.True(process.ExitCode == expectedExitCode, $"exit {process.ExitCode}: {await stderr}");
    }

    internal static string? FindBash()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? [@"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files\Git\usr\bin\bash.exe"]
            : ["/bin/bash", "/usr/bin/bash"];
        return candidates.FirstOrDefault(File.Exists);
    }
}
