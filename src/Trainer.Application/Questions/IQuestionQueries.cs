using Trainer.Domain.Keys;

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
}
