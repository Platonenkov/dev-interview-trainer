using System.Text.Json;
using System.Text.RegularExpressions;
using Trainer.TestSupport;

namespace Trainer.Deploy.Tests;

/// <summary>
/// The committed audit trail of the content waves must not carry fetched third-party page text: the repository is
/// public and its content is CC BY-SA. tools/content-wave/wave.py keeps the text under the git-ignored
/// <c>docs/content-wave-*/.local/</c> and commits only a sha256 marker in its place.
/// </summary>
public sealed partial class ContentWaveAuditTests
{
    [GeneratedRegex(@"^\[third-party text removed; sha256 [0-9a-f]{64}\]$")]
    private static partial Regex Marker();

    // Inside a JSON string a task item's excerpt line starts with an escaped newline.
    [GeneratedRegex(@"\\n  excerpt: (?!\[third-party text removed; sha256 [0-9a-f]{64}\])")]
    private static partial Regex UnredactedTaskExcerpt();

    private static IEnumerable<string> CommittedWaveFiles(string pattern)
    {
        string docs = Path.Combine(ContentWorkspace.RepositoryRoot(), "docs");
        return Directory.EnumerateDirectories(docs, "content-wave-*")
            .SelectMany(wave => Directory.EnumerateFiles(wave, pattern, SearchOption.AllDirectories)
                .Where(file => !Path.GetRelativePath(wave, file).Replace('\\', '/').StartsWith(".local/", StringComparison.Ordinal)));
    }

    [Fact]
    public void LinkEvidence_HoldsNoPageText()
    {
        List<string> offenders = [];
        foreach (string file in CommittedWaveFiles("*.json").Where(f => f.Replace('\\', '/').Contains("/evidence/links/", StringComparison.Ordinal)))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
            if (!document.RootElement.TryGetProperty("excerpts", out JsonElement excerpts))
            {
                continue;
            }

            foreach (JsonProperty excerpt in excerpts.EnumerateObject())
            {
                if (excerpt.Value.ValueKind != JsonValueKind.String || !Marker().IsMatch(excerpt.Value.GetString()!))
                {
                    offenders.Add($"{file}: excerpts.{excerpt.Name}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void TaskFiles_HoldNoPageText()
    {
        List<string> offenders = CommittedWaveFiles("*.json")
            .Where(file => UnredactedTaskExcerpt().IsMatch(File.ReadAllText(file)))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void LocalCache_IsIgnoredByGit()
    {
        string[] lines = File.ReadAllLines(Path.Combine(ContentWorkspace.RepositoryRoot(), ".gitignore"));

        Assert.Contains("docs/content-wave-*/.local/", lines);
    }
}
