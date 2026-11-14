using Trainer.Application.Catalog;
using Trainer.Domain.Keys;
using Trainer.Domain.Questions;

namespace Trainer.Application.Questions;

public interface IQuestionQueries
{
    /// <summary>
    /// The question in the culture, retired ones included (they stay readable). Null when the slug is unknown or the
    /// question has no locale for the culture.
    /// </summary>
    Task<QuestionPageModel?> GetAsync(CultureKey culture, StackKey stack, Slug slug, CancellationToken ct);

    /// <summary>The slug that replaced <paramref name="from"/>, or null when no redirect entry exists.</summary>
    Task<Slug?> GetRedirectAsync(StackKey stack, Slug from, CancellationToken ct);

    /// <summary>
    /// Up to <paramref name="limit"/> live questions of the stack in the culture that share at least one tag with the
    /// question <paramref name="id"/>, never the question itself. Ordered by the number of shared tags (most first),
    /// then questions of the same topic, then title. Retired questions, topics and stacks and questions without a
    /// locale for the culture are left out.
    /// </summary>
    Task<IReadOnlyList<CatalogRow>> GetRelatedAsync(CultureKey culture, StackKey stack, QuestionId id, int limit, CancellationToken ct);
}
