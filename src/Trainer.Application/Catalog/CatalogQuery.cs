using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;

namespace Trainer.Application.Catalog;

/// <summary>
/// A catalog request. An empty list means "no filter" for that dimension; within one dimension the values are
/// alternatives (OR), across dimensions they combine (AND). <paramref name="Search"/> is already normalised (trimmed,
/// whitespace collapsed, bounded); it narrows the result further and is null when there is no search.
/// </summary>
public sealed record CatalogQuery(
    CultureKey Culture,
    StackKey Stack,
    IReadOnlyList<Level> Levels,
    IReadOnlyList<TopicKey> Topics,
    IReadOnlyList<QuestionType> Types,
    int Page,
    string? Search = null)
{
    public const int PageSize = 25;
}
