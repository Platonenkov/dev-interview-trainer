using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;

namespace Trainer.Application.Catalog;

/// <summary>
/// A catalog request. An empty list means "no filter" for that dimension; within one dimension the values are
/// alternatives (OR), across dimensions they combine (AND).
/// </summary>
public sealed record CatalogQuery(
    CultureKey Culture,
    StackKey Stack,
    IReadOnlyList<Level> Levels,
    IReadOnlyList<TopicKey> Topics,
    IReadOnlyList<QuestionType> Types,
    int Page)
{
    public const int PageSize = 25;
}
