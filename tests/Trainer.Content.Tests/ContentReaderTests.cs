using Trainer.Content.Reading;

namespace Trainer.Content.Tests;

public sealed class ContentReaderTests
{
    private static Task<ContentSet> Read(string root) => new ContentReader().ReadAsync(root, CancellationToken.None);

    [Fact]
    public async Task ReadAsync_ValidFixture_ReadsAllParts()
    {
        ContentSet set = await Read(Fixtures.Valid);

        Assert.Equal(1, set.SchemaVersion);
        Assert.Equal(6, set.Questions.Count);
        Assert.Equal(3, set.TopicsByStack["dotnet"].Count);
        Assert.Single(set.Stacks);
        Assert.Empty(set.RedirectsByStack["dotnet"]);
        Assert.Empty(set.ReadIssues);

        SourcedQuestion output = Assert.Single(set.Questions, q => q.Document.Type == "output");
        Assert.Equal(("output", "333"), (output.Document.Expect!.Kind, output.Document.Expect.Value));
        Assert.Equal("dotnet/csharp/closure-for-loop.yaml", output.RelativePath);
        Assert.Equal(new DateOnly(2026, 11, 11), output.Document.Meta.Verified);
    }

    [Fact]
    public async Task ReadAsync_ValidFixture_ReadsEveryQuestionType()
    {
        ContentSet set = await Read(Fixtures.Valid);

        string[] types = [.. set.Questions.Select(q => q.Document.Type).Order(StringComparer.Ordinal)];
        Assert.Equal(["find-bug", "multi", "open", "output", "single", "situation"], types);
        Assert.All(set.Questions, q => Assert.Equal(["en", "ru"], q.Document.Locales.Keys.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public async Task ReadAsync_BrokenFixture_ReportsEachProblemAndKeepsGoodFile()
    {
        ContentSet set = await Read(Fixtures.Broken);

        Assert.Single(set.Questions);
        Assert.Contains(set.ReadIssues, i => i.Code == "yaml-parse" && i.RelativePath.EndsWith("syntax-error.yaml", StringComparison.Ordinal));
        Assert.Contains(set.ReadIssues, i => i.Code == "content-structure" && i.RelativePath.EndsWith("unknown-property.yaml", StringComparison.Ordinal));
        Assert.Contains(set.ReadIssues, i => i.Code == "content-structure" && i.RelativePath.EndsWith("missing-title.yaml", StringComparison.Ordinal));
        Assert.Contains(set.ReadIssues, i => i.Code == "content-structure" && i.RelativePath.EndsWith("numeric-level.yaml", StringComparison.Ordinal));
        Assert.Contains(set.ReadIssues, i => i.Code == "content-structure" && i.RelativePath.EndsWith("bad-date.yaml", StringComparison.Ordinal));
        Assert.Contains(set.ReadIssues, i => i.Code == "yaml-alias-forbidden" && i.RelativePath.EndsWith("alias.yaml", StringComparison.Ordinal));
        Assert.Contains(set.ReadIssues, i => i.Code == "yaml-multiple-documents" && i.RelativePath.EndsWith("multi-doc.yaml", StringComparison.Ordinal));
        Assert.Equal(7, set.ReadIssues.Count);
    }

    [Fact]
    public async Task ReadAsync_ContentStructureIssue_CarriesJsonPath()
    {
        ContentSet set = await Read(Fixtures.Broken);

        ContentIssueAssert.HasJsonPath(set, "missing-title.yaml", "locales.en");
        ContentIssueAssert.HasJsonPath(set, "numeric-level.yaml", "level");
    }

    [Fact]
    public async Task ReadAsync_YmlFile_IsReportedInsteadOfIgnored()
    {
        string root = Path.Combine(Path.GetTempPath(), "dit-content-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyDirectory(Fixtures.Valid, root, crlfAndBom: false);
            File.WriteAllText(Path.Combine(root, "dotnet", "async", "wrong-extension.yml"), "id: x");

            ContentSet set = await Read(root);

            Assert.Contains(set.ReadIssues, i => i.Code == "yaml-extension" && i.RelativePath == "dotnet/async/wrong-extension.yml");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_SameBytesWithCrlfAndBom_GivesSameHash()
    {
        string root = Path.Combine(Path.GetTempPath(), "dit-content-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyDirectory(Fixtures.Valid, root, crlfAndBom: true);

            ContentSet original = await Read(Fixtures.Valid);
            ContentSet converted = await Read(root);

            Assert.Empty(converted.ReadIssues);
            Assert.Equal(original.Hash, converted.Hash);
            Assert.Equal(original.Questions.Count, converted.Questions.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_MissingRoot_Throws()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => Read(Path.Combine(AppContext.BaseDirectory, "no-such-content")));
    }

    private static void CopyDirectory(string source, string target, bool crlfAndBom)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            string text = File.ReadAllText(file);
            if (crlfAndBom)
            {
                // Normalise first: a CRLF checkout must not become CR CR LF.
                text = "﻿" + text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
            }

            File.WriteAllText(Path.Combine(target, Path.GetFileName(file)), text);
        }

        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)), crlfAndBom);
        }
    }
}

internal static class ContentIssueAssert
{
    public static void HasJsonPath(ContentSet set, string fileName, string pathContains) =>
        Assert.Contains(
            set.ReadIssues,
            i => i.RelativePath.EndsWith(fileName, StringComparison.Ordinal)
                && i.JsonPath is not null
                && i.JsonPath.Contains(pathContains, StringComparison.Ordinal));
}
