using System.Text.RegularExpressions;

namespace Trainer.Snippets.Tests;

/// <summary>
/// Keeps the image that the content checks pre-pull (content-checks.yml, called by ci and deploy-content) identical to the one the
/// runner would otherwise pull inside its build deadline.
/// </summary>
public sealed partial class CiWorkflowDriftTests
{
    [GeneratedRegex(@"^\s*SANDBOX_IMAGE:\s*(?<image>\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex SandboxImageLine();

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("global.json was not found above the test output directory.");
    }

    [Fact]
    public async Task CiSandboxImage_MatchesTheDefaultSandboxImage()
    {
        string workflow = await File.ReadAllTextAsync(
            Path.Combine(FindRepositoryRoot(), ".github", "workflows", "content-checks.yml"), TestContext.Current.CancellationToken);

        Match match = SandboxImageLine().Match(workflow);

        Assert.True(match.Success, "content-checks.yml must define SANDBOX_IMAGE.");
        Assert.Equal(new SandboxOptions().Image, match.Groups["image"].Value);
    }
}
