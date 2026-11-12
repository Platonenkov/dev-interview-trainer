using Trainer.Content.Model;
using Trainer.Content.Validation;

namespace Trainer.Content.Reading;

/// <summary>A parsed question together with the content-root-relative path it came from (forward slashes).</summary>
public sealed record SourcedQuestion(string RelativePath, QuestionDocument Document);

/// <summary>Everything read from a content root. Bad files are reported in <see cref="ReadIssues"/> and skipped.</summary>
public sealed record ContentSet(
    int SchemaVersion,
    IReadOnlyList<StackDocument> Stacks,
    IReadOnlyDictionary<string, IReadOnlyList<TopicDocument>> TopicsByStack,
    IReadOnlyList<SourcedQuestion> Questions,
    IReadOnlyDictionary<string, IReadOnlyList<RedirectDocument>> RedirectsByStack,
    string Hash,
    IReadOnlyList<ContentIssue> ReadIssues);

public interface IContentReader
{
    /// <exception cref="DirectoryNotFoundException"><paramref name="rootPath"/> does not exist.</exception>
    Task<ContentSet> ReadAsync(string rootPath, CancellationToken ct);
}
