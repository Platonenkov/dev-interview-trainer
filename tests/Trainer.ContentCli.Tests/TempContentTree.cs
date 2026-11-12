namespace Trainer.ContentCli.Tests;

/// <summary>A throw-away content root with just enough structure for the reader to accept it.</summary>
internal sealed class TempContentTree : IDisposable
{
    public TempContentTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "dotnet", "async"));
        File.WriteAllText(Path.Combine(Root, "schema-version"), "1\n");
        File.WriteAllText(Path.Combine(Root, "stacks.yaml"), """
            - key: dotnet
              locales:
                ru: { title: .NET, description: Вопросы по .NET. }
                en: { title: .NET, description: .NET questions. }
            """);
        File.WriteAllText(Path.Combine(Root, "dotnet", "topics.yaml"), """
            - key: async
              locales:
                ru: { title: async, description: Задачи. }
                en: { title: async, description: Tasks. }
            """);
    }

    public string Root { get; }

    /// <summary>Writes <c>dotnet/async/{slug}.yaml</c> citing <paramref name="linkUrls"/> and embedding <paramref name="markdownUrl"/> in the English answer.</summary>
    public void AddQuestion(string slug, string ulid, IEnumerable<string> linkUrls, string? markdownUrl)
    {
        string links = string.Join("\n", linkUrls.Select(url => $"  - url: {url}\n    lang: en\n    kind: article"));
        string titles = string.Join("\n", linkUrls.Select((_, i) => $"      - Title {i}"));
        string markdown = markdownUrl is null ? "No links here." : $"See [the docs]({markdownUrl}) and <{markdownUrl}#anchor> for details.";
        string yaml = $"""
            id: {ulid}
            slug: {slug}
            stack: dotnet
            topic: async
            level: middle
            type: open
            grading: self
            tags: [test]
            links:
            {links}
            locales:
              ru:
                title: Заголовок
                prompt: Вопрос
                answer: Ответ
                explanation: Объяснение
                linkTitles:
            {titles}
              en:
                title: Title
                prompt: Question
                answer: {markdown}
                explanation: Explanation
                linkTitles:
            {titles}
            meta:
              authors: [tester]
              verified: 2026-11-11
            """;
        File.WriteAllText(Path.Combine(Root, "dotnet", "async", slug + ".yaml"), yaml);
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
