using Trainer.Content.Reading;

namespace Trainer.Content.Validation;

public interface IContentValidator
{
    /// <summary>
    /// Checks the semantic rules that strict deserialization cannot express and returns every violation found.
    /// Problems from reading (<c>ContentSet.ReadIssues</c>) are not repeated here; callers combine both lists.
    /// </summary>
    IReadOnlyList<ContentIssue> Validate(ContentSet content);
}
