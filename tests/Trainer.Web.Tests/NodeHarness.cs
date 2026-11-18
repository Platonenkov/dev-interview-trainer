using System.Diagnostics;
using System.Text.Json;
using Trainer.TestSupport;

namespace Trainer.Web.Tests;

/// <summary>
/// Runs a browser script under Node with a hand-made <c>window</c>, <c>document</c>, <c>location</c> and
/// <c>navigator</c>, so the behaviour of small scripts (the recovery handler, the self-hosted counter) is tested
/// without a browser. The harness prints one JSON document; the scripts under test are read from the repository.
/// </summary>
public static class NodeHarness
{
    private static readonly string? NodePath = FindNode();

    public static bool IsAvailable => NodePath is not null;

    public static string WwwRootFile(params string[] relative) =>
        Path.Combine([ContentWorkspace.RepositoryRoot(), "src", "Trainer.Web", "wwwroot", .. relative]);

    /// <summary>Runs <paramref name="harness"/> (JavaScript) with <paramref name="args"/> and parses what it prints.</summary>
    public static async Task<JsonDocument> RunAsync(string harness, CancellationToken cancellationToken, params string[] args)
    {
        if (!IsAvailable)
        {
            // CI installs Node (ci.yml); a missing Node there must not turn into silently skipped coverage.
            Assert.False(
                MissingNodeIsFailure(Environment.GetEnvironmentVariable("CI")),
                "Node.js is not installed on CI, so the browser-script behaviour tests cannot run.");
            Assert.Skip("Node.js is not installed, so the browser-script behaviour tests cannot run.");
        }

        string file = Path.Combine(Path.GetTempPath(), "dit-harness-" + Guid.NewGuid().ToString("N") + ".js");
        await File.WriteAllTextAsync(file, harness, cancellationToken);
        try
        {
            ProcessStartInfo info = new(NodePath!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add(file);
            foreach (string arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(info)!;
            Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            Assert.True(process.ExitCode == 0, "node failed: " + await error);
            return JsonDocument.Parse(output);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>GitHub Actions sets <c>CI=true</c>; there a missing Node fails the test, on a developer PC it skips it.</summary>
    public static bool MissingNodeIsFailure(string? ciVariable) => string.Equals(ciVariable, "true", StringComparison.OrdinalIgnoreCase);

    private static string? FindNode()
    {
        string name = OperatingSystem.IsWindows() ? "node.exe" : "node";
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
