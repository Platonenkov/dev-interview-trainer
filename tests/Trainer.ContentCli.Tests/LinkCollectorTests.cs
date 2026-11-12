using Trainer.Content.Reading;
using Trainer.ContentCli.Links;

namespace Trainer.ContentCli.Tests;

public sealed class LinkCollectorTests
{
    [Fact]
    public async Task Collect_MergesLinksAndMarkdownAndDeduplicatesAcrossQuestions()
    {
        using TempContentTree tree = new();
        tree.AddQuestion("first", "01KBZ3Q8N9V2K3M4P5R6S7T8W1", ["https://example.test/shared", "https://example.test/only-first"], "https://example.test/in-markdown");
        tree.AddQuestion("second", "01KBZ3Q8N9V2K3M4P5R6S7T8W2", ["https://example.test/shared"], "https://example.test/shared#section");
        ContentSet set = await new ContentReader().ReadAsync(tree.Root, TestContext.Current.CancellationToken);
        Assert.Empty(set.ReadIssues);

        IReadOnlyDictionary<string, IReadOnlyList<string>> found = new LinkCollector().Collect(set);

        Assert.Equal(
            ["https://example.test/in-markdown", "https://example.test/only-first", "https://example.test/shared"],
            found.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["dotnet/async/first.yaml", "dotnet/async/second.yaml"], found["https://example.test/shared"]);
        Assert.Equal(["dotnet/async/first.yaml"], found["https://example.test/only-first"]);
        Assert.Equal(["dotnet/async/first.yaml"], found["https://example.test/in-markdown"]);
    }

    [Fact]
    public async Task Collect_SkipsRelativeAndNonHttpsUrls()
    {
        using TempContentTree tree = new();
        tree.AddQuestion("only", "01KBZ3Q8N9V2K3M4P5R6S7T8W3", ["http://example.test/plain", "/en/dotnet/other"], "mailto:someone@example.test");
        ContentSet set = await new ContentReader().ReadAsync(tree.Root, TestContext.Current.CancellationToken);

        IReadOnlyDictionary<string, IReadOnlyList<string>> found = new LinkCollector().Collect(set);

        Assert.Empty(found);
    }
}
