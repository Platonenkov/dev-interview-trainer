using Trainer.Domain.Feedback;
using Trainer.Domain.Keys;
using Trainer.Domain.Questions;

namespace Trainer.Application.Feedback;

public enum FeedbackOutcome
{
    Saved,
    QuestionNotFound,
}

/// <param name="Outcome">Whether the rating was stored.</param>
/// <param name="Rating">The stored rating after the call; null when it was cleared or the question was not found.</param>
public sealed record RatingResult(FeedbackOutcome Outcome, QuestionRating? Rating);

public enum ReportOutcome
{
    Created,
    Updated,
    QuestionNotFound,
    LimitReached,
}

/// <param name="Outcome">What the call did.</param>
/// <param name="RetryAt">Set with <see cref="ReportOutcome.LimitReached"/>: when the oldest counted report leaves the window.</param>
public sealed record ReportResult(ReportOutcome Outcome, DateTimeOffset? RetryAt);

public sealed record ReportRow(
    ReportId Id,
    QuestionId QuestionId,
    string Stack,
    string Slug,
    string Title,
    CultureKey Culture,
    ReportReason Reason,
    string? Comment,
    long ReporterTelegramId,
    string? ReporterName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt);

public sealed record ReportPage(IReadOnlyList<ReportRow> Rows, int Page, int PageCount);

public sealed record RatingTotals(QuestionId QuestionId, string Stack, string Slug, string Title, int Likes, int Dislikes);
