using Trainer.Domain.Feedback;
using Trainer.Domain.Keys;
using Trainer.Domain.Questions;
using Trainer.Domain.Users;

namespace Trainer.Application.Feedback;

/// <summary>Reads question feedback for the question page, the practice islands and the admin page (ADR 0016).</summary>
public interface IQuestionFeedbackQueries
{
    const int ReportsPerPage = 25;

    /// <summary>The user's ratings among <paramref name="questions"/>; questions the user has not rated are absent.</summary>
    Task<IReadOnlyDictionary<QuestionId, QuestionRating>> GetMyRatingsAsync(
        UserId user, IReadOnlyCollection<QuestionId> questions, CancellationToken ct);

    /// <summary>Open or resolved reports, newest first, <see cref="ReportsPerPage"/> per page. A page out of range is clamped.</summary>
    Task<ReportPage> GetReportsAsync(bool resolved, int page, CancellationToken ct);

    /// <summary>
    /// Questions with at least one dislike, most disliked first. Titles are in <paramref name="culture"/> when the question
    /// has that locale, else in the default culture.
    /// </summary>
    Task<IReadOnlyList<RatingTotals>> GetMostDislikedAsync(int count, CultureKey culture, CancellationToken ct);

    /// <summary>
    /// True when the question takes feedback: not retired, in a topic and a stack that are not retired. This is the rule
    /// <see cref="IQuestionFeedbackService"/> enforces, so a page can offer the forms only where a post can succeed.
    /// </summary>
    Task<bool> IsOpenForFeedbackAsync(QuestionId question, CancellationToken ct);
}
