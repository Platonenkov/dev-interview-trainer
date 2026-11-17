using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Trainer.Application.Feedback;
using Trainer.Domain.Feedback;
using Trainer.Domain.Ids;
using Trainer.Domain.Keys;
using Trainer.Domain.Questions;
using Trainer.Domain.Users;
using Trainer.Infrastructure.Persistence;
using Trainer.Infrastructure.Persistence.Entities;

namespace Trainer.Infrastructure.Feedback;

/// <summary>
/// Stores ratings and reports (ADR 0016). A rating is one upsert, so a double click stores one row; a report takes a
/// per-user advisory lock, so the daily quota holds under parallel requests. Nothing here logs a comment.
/// </summary>
internal sealed class QuestionFeedbackService(
    TrainerDbContext db,
    IOptions<FeedbackOptions> options,
    UlidGenerator ids,
    TimeProvider time) : IQuestionFeedbackService
{
    internal const string LockPrefix = "feedback:";

    private static readonly TimeSpan QuotaWindow = TimeSpan.FromHours(24);

    public async Task<RatingResult> SetRatingAsync(UserId user, QuestionId question, QuestionRating? rating, CancellationToken ct)
    {
        if (rating is { } value && !Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(nameof(rating), rating, null);
        }

        string userId = user.Value;
        string questionId = question.Value;
        if (rating is null)
        {
            // A rating given before the question was retired must stay removable, so clearing skips the availability check.
            await db.QuestionRatings.Where(r => r.UserId == userId && r.QuestionId == questionId).ExecuteDeleteAsync(ct);
            return new RatingResult(FeedbackOutcome.Saved, null);
        }

        if (!await IsAvailableAsync(question, ct))
        {
            return new RatingResult(FeedbackOutcome.QuestionNotFound, null);
        }

        DateTimeOffset now = StoredTime.Normalize(time.GetUtcNow());
        short stored = (short)rating.Value;
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO question_ratings (user_id, question_id, value, updated_at)
            VALUES ({userId}, {questionId}, {stored}, {now})
            ON CONFLICT (user_id, question_id) DO UPDATE SET value = EXCLUDED.value, updated_at = EXCLUDED.updated_at
            """,
            ct);
        return new RatingResult(FeedbackOutcome.Saved, rating);
    }

    public async Task<ReportResult> ReportAsync(
        UserId user, QuestionId question, CultureKey culture, ReportReason reason, string? comment, CancellationToken ct)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, null);
        }

        // The contract is enforced here, not left to callers: an over-long or NUL-carrying comment would otherwise fail
        // inside the transaction, and an un-normalised blank would be stored instead of null.
        if (!ReportComment.TryNormalise(comment, out string? normalisedComment))
        {
            throw new ArgumentException($"The comment is longer than {ReportComment.MaxLength} characters.", nameof(comment));
        }

        comment = normalisedComment;

        int limit = options.Value.ReportsPerDay;
        string userId = user.Value;
        string questionId = question.Value;
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(ct);

        // Serialises one user's reports so the count below cannot be passed by parallel requests. A hash collision only
        // serialises two users.
        string lockKey = LockPrefix + userId;
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);

        if (!await IsAvailableAsync(question, ct))
        {
            return new ReportResult(ReportOutcome.QuestionNotFound, null);
        }

        string cultureValue = culture.Value;
        int updated = await db.QuestionReports
            .Where(r => r.UserId == userId && r.QuestionId == questionId && r.Reason == reason && r.ResolvedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Comment, comment).SetProperty(r => r.Culture, cultureValue), ct);
        if (updated > 0)
        {
            await transaction.CommitAsync(ct);
            return new ReportResult(ReportOutcome.Updated, null);
        }

        DateTimeOffset now = StoredTime.Normalize(time.GetUtcNow());
        DateTimeOffset windowStart = now - QuotaWindow;
        List<DateTimeOffset> counted = await db.QuestionReports
            .AsNoTracking()
            .Where(r => r.UserId == userId && r.CreatedAt > windowStart)
            .OrderBy(r => r.CreatedAt)
            .Select(r => r.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);
        if (counted.Count >= limit)
        {
            return new ReportResult(ReportOutcome.LimitReached, counted[0] + QuotaWindow);
        }

        db.QuestionReports.Add(new QuestionReportEntity
        {
            Id = ids.Next(),
            UserId = userId,
            QuestionId = questionId,
            Culture = cultureValue,
            Reason = reason,
            Comment = comment,
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new ReportResult(ReportOutcome.Created, null);
    }

    public async Task<bool> ResolveReportAsync(ReportId report, UserId admin, CancellationToken ct)
    {
        string reportId = report.Value;
        string adminId = admin.Value;
        DateTimeOffset now = StoredTime.Normalize(time.GetUtcNow());
        int resolved = await db.QuestionReports
            .Where(r => r.Id == reportId && r.ResolvedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ResolvedAt, now).SetProperty(r => r.ResolvedBy, adminId), ct);
        return resolved > 0;
    }

    /// <summary>
    /// A question takes feedback while it is live: not retired itself, and in a topic and a stack that are not retired,
    /// the same rule as answers and the public pages.
    /// </summary>
    private async Task<bool> IsAvailableAsync(QuestionId question, CancellationToken ct)
    {
        string questionId = question.Value;
        return await AvailableQuestions(db).AnyAsync(q => q.Id == questionId, ct);
    }

    /// <summary>Questions that are not retired and sit in a topic and a stack that are not retired.</summary>
    internal static IQueryable<QuestionEntity> AvailableQuestions(TrainerDbContext db) =>
        db.Questions
            .AsNoTracking()
            .Where(q => q.RetiredAt == null
                && !db.Stacks.Any(s => s.Key == q.StackKey && s.RetiredAt != null)
                && !db.Topics.Any(t => t.StackKey == q.StackKey && t.Key == q.TopicKey && t.RetiredAt != null));
}
