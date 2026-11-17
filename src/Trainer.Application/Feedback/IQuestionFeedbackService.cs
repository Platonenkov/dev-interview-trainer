using Trainer.Domain.Feedback;
using Trainer.Domain.Keys;
using Trainer.Domain.Questions;
using Trainer.Domain.Users;

namespace Trainer.Application.Feedback;

/// <summary>Writes question ratings and reports of signed-in users (ADR 0016).</summary>
public interface IQuestionFeedbackService
{
    /// <summary>
    /// Stores the user's rating of a question; <c>null</c> clears it. Repeating a call is harmless. A retired or unknown
    /// question gives <see cref="FeedbackOutcome.QuestionNotFound"/>.
    /// </summary>
    Task<RatingResult> SetRatingAsync(UserId user, QuestionId question, QuestionRating? rating, CancellationToken ct);

    /// <summary>
    /// Files a report. An open report of the same user with the same question and reason is updated instead (its comment
    /// and culture), and does not count against <see cref="FeedbackOptions.ReportsPerDay"/>. The service normalises
    /// <paramref name="comment"/> with <see cref="ReportComment.TryNormalise"/> itself and stores the result.
    /// </summary>
    /// <exception cref="ArgumentException">The comment is longer than <see cref="ReportComment.MaxLength"/> after normalising.</exception>
    Task<ReportResult> ReportAsync(
        UserId user, QuestionId question, CultureKey culture, ReportReason reason, string? comment, CancellationToken ct);

    /// <summary>Marks an open report resolved. False when the report does not exist or is already resolved.</summary>
    Task<bool> ResolveReportAsync(ReportId report, UserId admin, CancellationToken ct);
}
