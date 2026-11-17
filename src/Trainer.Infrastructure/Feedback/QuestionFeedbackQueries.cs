using Microsoft.EntityFrameworkCore;
using Trainer.Application.Feedback;
using Trainer.Domain.Feedback;
using Trainer.Domain.Keys;
using Trainer.Domain.Questions;
using Trainer.Domain.Users;
using Trainer.Infrastructure.Persistence;
using Trainer.Infrastructure.Persistence.Entities;

namespace Trainer.Infrastructure.Feedback;

internal sealed class QuestionFeedbackQueries(TrainerDbContext db) : IQuestionFeedbackQueries
{
    public async Task<IReadOnlyDictionary<QuestionId, QuestionRating>> GetMyRatingsAsync(
        UserId user, IReadOnlyCollection<QuestionId> questions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(questions);
        if (questions.Count == 0)
        {
            return new Dictionary<QuestionId, QuestionRating>();
        }

        string userId = user.Value;
        string[] questionIds = [.. questions.Select(q => q.Value).Distinct(StringComparer.Ordinal)];
        var rows = await db.QuestionRatings
            .AsNoTracking()
            .Where(r => r.UserId == userId && questionIds.Contains(r.QuestionId))
            .Select(r => new { r.QuestionId, r.Value })
            .ToListAsync(ct);
        return rows.ToDictionary(r => new QuestionId(r.QuestionId), r => r.Value);
    }

    public async Task<bool> IsOpenForFeedbackAsync(QuestionId question, CancellationToken ct)
    {
        string questionId = question.Value;
        return await QuestionFeedbackService.AvailableQuestions(db).AnyAsync(q => q.Id == questionId, ct);
    }

    public async Task<ReportPage> GetReportsAsync(bool resolved, int page, CancellationToken ct)
    {
        IQueryable<QuestionReportEntity> filtered = db.QuestionReports
            .AsNoTracking()
            .Where(r => resolved ? r.ResolvedAt != null : r.ResolvedAt == null);

        int total = await filtered.CountAsync(ct);
        int pageCount = Math.Max(1, (total + IQuestionFeedbackQueries.ReportsPerPage - 1) / IQuestionFeedbackQueries.ReportsPerPage);
        int current = Math.Clamp(page, 1, pageCount);

        // Page first, join after: the joins then only touch one page. The order is repeated after the join in SQL, which
        // does not promise to keep a subquery's order.
        var found = await (
            from report in filtered
                .OrderByDescending(r => r.CreatedAt)
                .ThenByDescending(r => r.Id)
                .Skip((current - 1) * IQuestionFeedbackQueries.ReportsPerPage)
                .Take(IQuestionFeedbackQueries.ReportsPerPage)
            join user in db.Users on report.UserId equals user.Id
            join question in db.Questions on report.QuestionId equals question.Id
            orderby report.CreatedAt descending, report.Id descending
            select new
            {
                report.Id,
                report.QuestionId,
                question.StackKey,
                question.Slug,
                report.Culture,
                report.Reason,
                report.Comment,
                user.TelegramUserId,
                ReporterName = user.DisplayName ?? user.Username,
                report.CreatedAt,
                report.ResolvedAt,
            })
            .ToListAsync(ct);
        List<StoredReport> rows = [.. found.Select(r => new StoredReport(
            r.Id, r.QuestionId, r.StackKey, r.Slug, r.Culture, r.Reason, r.Comment, r.TelegramUserId, r.ReporterName, r.CreatedAt, r.ResolvedAt))];

        Dictionary<string, Dictionary<string, string>> titles = await TitlesAsync([.. rows.Select(r => r.QuestionId)], ct);
        List<ReportRow> result = [.. rows.Select(r => new ReportRow(
            new ReportId(r.Id),
            new QuestionId(r.QuestionId),
            r.Stack,
            r.Slug,
            TitleOf(titles, r.QuestionId, r.Culture, r.Slug),
            new CultureKey(r.Culture),
            r.Reason,
            r.Comment,
            r.ReporterTelegramId,
            r.ReporterName,
            r.CreatedAt,
            r.ResolvedAt))];
        return new ReportPage(result, current, pageCount);
    }

    public async Task<IReadOnlyList<RatingTotals>> GetMostDislikedAsync(int count, CultureKey culture, CancellationToken ct)
    {
        if (count <= 0)
        {
            return [];
        }

        var top = await db.QuestionRatings
            .AsNoTracking()
            .Where(r => QuestionFeedbackService.AvailableQuestions(db).Any(q => q.Id == r.QuestionId))
            .GroupBy(r => r.QuestionId)
            .Select(g => new
            {
                QuestionId = g.Key,
                Likes = g.Count(r => r.Value == QuestionRating.Like),
                Dislikes = g.Count(r => r.Value == QuestionRating.Dislike),
            })
            .Where(t => t.Dislikes > 0)
            .OrderByDescending(t => t.Dislikes)
            .ThenBy(t => t.QuestionId)
            .Take(count)
            .ToListAsync(ct);
        if (top.Count == 0)
        {
            return [];
        }

        string[] ids = [.. top.Select(t => t.QuestionId)];
        Dictionary<string, (string Stack, string Slug)> questions = await db.Questions
            .AsNoTracking()
            .Where(q => ids.Contains(q.Id))
            .Select(q => new { q.Id, q.StackKey, q.Slug })
            .ToDictionaryAsync(q => q.Id, q => (q.StackKey, q.Slug), StringComparer.Ordinal, ct);
        Dictionary<string, Dictionary<string, string>> titles = await TitlesAsync(ids, ct);
        return [.. top.Select(t =>
        {
            (string stack, string slug) = questions[t.QuestionId];
            string title = TitleOf(titles, t.QuestionId, culture.Value, slug);
            return new RatingTotals(new QuestionId(t.QuestionId), stack, slug, title, t.Likes, t.Dislikes);
        })];
    }

    private async Task<Dictionary<string, Dictionary<string, string>>> TitlesAsync(string[] questionIds, CancellationToken ct)
    {
        if (questionIds.Length == 0)
        {
            return [];
        }

        var locales = await db.QuestionLocales
            .AsNoTracking()
            .Where(l => questionIds.Contains(l.QuestionId))
            .Select(l => new { l.QuestionId, l.Culture, l.Title })
            .ToListAsync(ct);
        return locales
            .GroupBy(l => l.QuestionId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToDictionary(l => l.Culture, l => l.Title, StringComparer.Ordinal), StringComparer.Ordinal);
    }

    /// <summary>The title in the preferred culture, else in the default culture, else in any, else the slug.</summary>
    private static string TitleOf(Dictionary<string, Dictionary<string, string>> titles, string questionId, string culture, string slug)
    {
        if (!titles.TryGetValue(questionId, out Dictionary<string, string>? byCulture))
        {
            return slug;
        }

        if (byCulture.TryGetValue(culture, out string? title) || byCulture.TryGetValue(CultureKey.Default.Value, out title))
        {
            return title;
        }

        return byCulture.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).FirstOrDefault() ?? slug;
    }

    private sealed record StoredReport(
        string Id,
        string QuestionId,
        string Stack,
        string Slug,
        string Culture,
        ReportReason Reason,
        string? Comment,
        long ReporterTelegramId,
        string? ReporterName,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ResolvedAt);
}
