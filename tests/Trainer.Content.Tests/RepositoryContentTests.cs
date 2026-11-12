using Trainer.Content.Reading;
using Trainer.Content.Rendering;
using Trainer.Content.Validation;
using Trainer.Domain.Questions;

namespace Trainer.Content.Tests;

public sealed class RepositoryContentTests
{
    [Fact]
    public async Task RepositoryContent_IsValid()
    {
        ContentSet set = await new ContentReader().ReadAsync(Fixtures.RepositoryContent, TestContext.Current.CancellationToken);
        ContentValidator validator = new(new ContentRenderer(), TimeProvider.System);

        List<ContentIssue> issues = [.. set.ReadIssues, .. validator.Validate(set)];

        Assert.True(
            issues.Count == 0,
            string.Join(Environment.NewLine, issues.Select(i => $"{i.RelativePath}: {i.Code}: {i.Message} [{i.JsonPath}]")));
    }

    [Fact]
    public async Task RepositoryContent_CoversAllTypes()
    {
        ContentSet set = await new ContentReader().ReadAsync(Fixtures.RepositoryContent, TestContext.Current.CancellationToken);

        HashSet<string> present = set.Questions.Select(q => q.Document.Type).ToHashSet(StringComparer.Ordinal);
        string[] missing = QuestionTypeKeys.All.Select(QuestionTypeKeys.ToKey).Where(k => !present.Contains(k)).ToArray();

        Assert.True(missing.Length == 0, $"Seed content has no question of type: {string.Join(", ", missing)}");
    }

    [Fact]
    public async Task RepositoryContent_DeclaresAllTwentyThreeTopics()
    {
        ContentSet set = await new ContentReader().ReadAsync(Fixtures.RepositoryContent, TestContext.Current.CancellationToken);

        Assert.Equal(23, set.TopicsByStack["dotnet"].Count);
    }
}
