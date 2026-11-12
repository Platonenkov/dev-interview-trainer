using System.Text.RegularExpressions;
using Trainer.Content.Reading;
using Trainer.Content.Rendering;
using Trainer.Content.Validation;
using Trainer.Domain.Questions;

namespace Trainer.Content.Tests;

/// <summary>
/// Keeps <c>docs/content-guide.md</c> honest. Every fenced block whose info string is exactly <c>yaml</c> is a complete
/// question file and must pass the real reader and validator; the issue-code table must list exactly the codes in the source.
/// </summary>
public sealed partial class ContentGuideExampleTests
{
    private const string QuestionBlockInfo = "yaml";

    private static readonly string GuidePath = Path.Combine(Fixtures.RepositoryRoot, "docs", "content-guide.md");

    // Plain kebab-case string constants. Every such constant in these files is an issue code.
    private static readonly string[] AllConstantsFiles =
    [
        "src/Trainer.Content/Validation/IssueCodes.cs",
        "src/Trainer.Content/Reading/ReadIssueCodes.cs",
        "src/Trainer.Snippets/SnippetVerifier.cs",
    ];

    // The import service also holds unrelated string constants, so only the ones named *Code count.
    private const string ImportServiceFile = "src/Trainer.Infrastructure/Content/ContentImportService.cs";

    [GeneratedRegex(@"^(stack|topic|slug|type):\s*(\S+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex HeaderField();

    [GeneratedRegex(@"const\s+string\s+(?<name>\w+)\s*=\s*""(?<code>[a-z][a-z0-9-]*)""\s*;", RegexOptions.CultureInvariant)]
    private static partial Regex StringConstant();

    [GeneratedRegex(@"^\|\s*`(?<code>[a-z][a-z0-9-]*)`\s*\|", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex TableRowCode();

    [Fact]
    public async Task ContentGuide_ExamplesAreValid()
    {
        List<string> examples = ExtractQuestionExamples(await File.ReadAllTextAsync(GuidePath, TestContext.Current.CancellationToken));

        Assert.NotEmpty(examples);
        List<string> failures = [];
        HashSet<string> typesSeen = new(StringComparer.Ordinal);
        foreach (string example in examples)
        {
            Dictionary<string, string> header = ReadHeader(example);
            typesSeen.Add(header["type"]);
            foreach (ContentIssue issue in await ValidateExampleAsync(example, header))
            {
                failures.Add($"{header["type"]} example: {issue.RelativePath}: {issue.Code}: {issue.Message} [{issue.JsonPath}]");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

        string[] missingTypes = QuestionTypeKeys.All.Select(QuestionTypeKeys.ToKey).Where(k => !typesSeen.Contains(k)).ToArray();
        Assert.True(missingTypes.Length == 0, $"The guide has no complete example for type: {string.Join(", ", missingTypes)}");
        Assert.Equal(examples.Count, typesSeen.Count);
    }

    [Fact]
    public async Task ContentGuide_IssueCodeTable_MatchesTheSource()
    {
        string guide = await File.ReadAllTextAsync(GuidePath, TestContext.Current.CancellationToken);
        HashSet<string> documented = TableRowCode().Matches(IssueCodeSection(guide)).Select(m => m.Groups["code"].Value).ToHashSet(StringComparer.Ordinal);

        HashSet<string> actual = new(StringComparer.Ordinal);
        foreach (string file in AllConstantsFiles)
        {
            IReadOnlyList<string> codes = await ReadConstantsAsync(file, namePredicate: _ => true);
            Assert.NotEmpty(codes);
            actual.UnionWith(codes);
        }

        IReadOnlyList<string> importCodes = await ReadConstantsAsync(ImportServiceFile, name => name.EndsWith("Code", StringComparison.Ordinal));
        Assert.NotEmpty(importCodes);
        actual.UnionWith(importCodes);

        string[] undocumented = actual.Except(documented).Order(StringComparer.Ordinal).ToArray();
        string[] stale = documented.Except(actual).Order(StringComparer.Ordinal).ToArray();

        Assert.True(undocumented.Length == 0, $"Issue codes missing from the guide table: {string.Join(", ", undocumented)}");
        Assert.True(stale.Length == 0, $"The guide table lists codes that no longer exist: {string.Join(", ", stale)}");
    }

    /// <summary>Field tables elsewhere in the guide also start rows with a backticked word, so only this section counts.</summary>
    private static string IssueCodeSection(string guide)
    {
        const string heading = "\n## Issue codes\n";
        int start = guide.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, "The guide has no \"## Issue codes\" section.");
        int end = guide.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        return end < 0 ? guide[start..] : guide[start..end];
    }

    private static async Task<IReadOnlyList<string>> ReadConstantsAsync(string repositoryRelativePath, Func<string, bool> namePredicate)
    {
        string text = await File.ReadAllTextAsync(Path.Combine(Fixtures.RepositoryRoot, repositoryRelativePath), TestContext.Current.CancellationToken);
        return StringConstant().Matches(text)
            .Where(m => namePredicate(m.Groups["name"].Value))
            .Select(m => m.Groups["code"].Value)
            .ToArray();
    }

    private static List<string> ExtractQuestionExamples(string markdown)
    {
        List<string> blocks = [];
        List<string>? current = null;
        foreach (string rawLine in markdown.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (current is null)
            {
                if (line.StartsWith("```", StringComparison.Ordinal) && string.Equals(line[3..].Trim(), QuestionBlockInfo, StringComparison.Ordinal))
                {
                    current = [];
                }

                continue;
            }

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                blocks.Add(string.Join('\n', current) + "\n");
                current = null;
                continue;
            }

            current.Add(line);
        }

        Assert.Null(current);
        return blocks;
    }

    private static Dictionary<string, string> ReadHeader(string example)
    {
        Dictionary<string, string> header = new(StringComparer.Ordinal);
        foreach (Match match in HeaderField().Matches(example))
        {
            header.TryAdd(match.Groups[1].Value, match.Groups[2].Value);
        }

        foreach (string key in new[] { "stack", "topic", "slug", "type" })
        {
            Assert.True(header.ContainsKey(key), $"A yaml example in the guide has no top-level '{key}'.");
        }

        return header;
    }

    private static async Task<List<ContentIssue>> ValidateExampleAsync(string example, Dictionary<string, string> header)
    {
        string root = Path.Combine(Path.GetTempPath(), "content-guide-" + Guid.NewGuid().ToString("N"));
        try
        {
            string stack = header["stack"];
            string topic = header["topic"];
            string questionDirectory = Path.Combine(root, stack, topic);
            Directory.CreateDirectory(questionDirectory);

            await File.WriteAllTextAsync(Path.Combine(root, "schema-version"), "1\n", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "stacks.yaml"), TaxonomyEntry(stack), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, stack, "topics.yaml"), TaxonomyEntry(topic), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(questionDirectory, header["slug"] + ".yaml"), example, TestContext.Current.CancellationToken);

            ContentSet set = await new ContentReader().ReadAsync(root, TestContext.Current.CancellationToken);
            ContentValidator validator = new(new ContentRenderer(), TimeProvider.System);
            return [.. set.ReadIssues, .. validator.Validate(set)];
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string TaxonomyEntry(string key) =>
        $"""
        - key: {key}
          locales:
            ru:
              title: Example
              description: Example
            en:
              title: Example
              description: Example

        """;
}
