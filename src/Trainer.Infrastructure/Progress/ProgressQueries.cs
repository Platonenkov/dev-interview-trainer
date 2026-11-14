using Microsoft.EntityFrameworkCore;
using Trainer.Application.Progress;
using Trainer.Contracts;
using Trainer.Domain.Grading;
using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;
using Trainer.Domain.Scheduling;
using Trainer.Domain.Users;
using Trainer.Infrastructure.Mock;
using Trainer.Infrastructure.Persistence;
using Trainer.Infrastructure.Persistence.Entities;
using Trainer.Infrastructure.Queries;

namespace Trainer.Infrastructure.Progress;

/// <summary>
/// Progress queries for one signed-in user. Counts run in SQL; the only streamed result is the streak, which stops reading
/// at the first gap.
/// </summary>
internal sealed class ProgressQueries(TrainerDbContext db, TimeProvider clock) : IProgressQueries
{
    private const int RecentMockCount = 5;

    public async Task<IReadOnlyDictionary<QuestionId, QuestionStatus>> GetStatusesAsync(
        UserId user, IReadOnlyCollection<QuestionId> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return new Dictionary<QuestionId, QuestionStatus>();
        }

        string userId = user.Value;
        string[] keys = [.. ids.Select(id => id.Value)];
        DateTimeOffset now = clock.GetUtcNow();
        var rows = await db.ReviewStates
            .AsNoTracking()
            .Where(r => r.UserId == userId && keys.Contains(r.QuestionId))
            .Select(r => new { r.QuestionId, r.LastGrade, r.DueAt })
            .ToListAsync(ct);

        return rows.ToDictionary(r => new QuestionId(r.QuestionId), r => new QuestionStatus(r.LastGrade, r.DueAt, r.DueAt <= now));
    }

    public async Task<int> GetDueCountAsync(UserId user, CultureKey culture, StackKey stack, CancellationToken ct)
    {
        string userId = user.Value;
        DateTimeOffset now = clock.GetUtcNow();
        return await LiveQuestions.Of(db, culture, stack)
            .Join(db.ReviewStates.Where(r => r.UserId == userId && r.DueAt <= now), q => q.Id, r => r.QuestionId, (q, _) => q.Id)
            .CountAsync(ct);
    }

    public async Task<ProfileDto> GetProfileAsync(UserId user, CultureKey culture, StackKey stack, CancellationToken ct)
    {
        Breakdown breakdown = await LoadBreakdownAsync(user, culture, stack, ct);
        int streak = await GetStreakAsync(user, ct);
        DateTimeOffset? nextDue = await GetNextDueAsync(user, culture, stack, ct);
        IReadOnlyList<MockHistoryDto> mocks = await GetRecentMocksAsync(user, stack, ct);
        return new ProfileDto(streak, breakdown.TotalAnswered, breakdown.Due, nextDue, breakdown.Topics, breakdown.Levels, mocks);
    }

    public async Task<MeDto> GetMeAsync(UserId user, CultureKey culture, StackKey stack, CancellationToken ct)
    {
        string userId = user.Value;
        long? telegramUserId = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => (long?)u.TelegramUserId)
            .FirstOrDefaultAsync(ct);
        if (telegramUserId is null)
        {
            throw new InvalidOperationException("The user does not exist.");
        }

        Breakdown breakdown = await LoadBreakdownAsync(user, culture, stack, ct);
        int streak = await GetStreakAsync(user, ct);
        return new MeDto(telegramUserId.Value, streak, breakdown.Due, breakdown.Topics);
    }

    private sealed record Breakdown(
        IReadOnlyList<TopicProgressDto> Topics, IReadOnlyList<LevelProgressDto> Levels, int TotalAnswered, int Due);

    private sealed record Cell(string Topic, Level Level, int Answered, int Known, int Due);

    private async Task<Breakdown> LoadBreakdownAsync(UserId user, CultureKey culture, StackKey stack, CancellationToken ct)
    {
        string userId = user.Value;
        DateTimeOffset now = clock.GetUtcNow();
        IQueryable<QuestionEntity> live = LiveQuestions.Of(db, culture, stack);

        var liveCounts = await live
            .GroupBy(q => new { q.TopicKey, q.Level })
            .Select(g => new { g.Key.TopicKey, g.Key.Level, Count = g.Count() })
            .ToListAsync(ct);

        List<Cell> answered = await live
            .Join(
                db.ReviewStates.Where(r => r.UserId == userId),
                q => q.Id,
                r => r.QuestionId,
                (q, r) => new { q.TopicKey, q.Level, r.LastGrade, r.DueAt })
            .GroupBy(x => new { x.TopicKey, x.Level })
            .Select(g => new Cell(
                g.Key.TopicKey,
                g.Key.Level,
                g.Count(),
                g.Count(x => x.LastGrade == Grade.Knew),
                g.Count(x => x.DueAt <= now)))
            .ToListAsync(ct);

        Dictionary<string, string> titles = await LiveQuestions.TopicTitlesAsync(db, culture, stack, ct);
        TopicProgressDto[] topics = [.. liveCounts
            .GroupBy(c => c.TopicKey, StringComparer.Ordinal)
            .Select(g =>
            {
                List<Cell> cells = [.. answered.Where(a => string.Equals(a.Topic, g.Key, StringComparison.Ordinal))];
                return new TopicProgressDto(
                    g.Key,
                    LiveQuestions.TitleOf(titles, g.Key),
                    g.Sum(c => c.Count),
                    cells.Sum(c => c.Answered),
                    cells.Sum(c => c.Known),
                    cells.Sum(c => c.Due));
            })
            .OrderBy(t => t.Title, LiveQuestions.TitleComparer(culture))
            .ThenBy(t => t.Topic, StringComparer.Ordinal)];

        LevelProgressDto[] levels = [.. LevelKeys.All.Select(level =>
        {
            List<Cell> cells = [.. answered.Where(a => a.Level == level)];
            return new LevelProgressDto(
                LevelKeys.ToKey(level),
                liveCounts.Where(c => c.Level == level).Sum(c => c.Count),
                cells.Sum(c => c.Answered),
                cells.Sum(c => c.Known));
        })];

        return new Breakdown(topics, levels, answered.Sum(c => c.Answered), answered.Sum(c => c.Due));
    }

    private async Task<DateTimeOffset?> GetNextDueAsync(UserId user, CultureKey culture, StackKey stack, CancellationToken ct)
    {
        string userId = user.Value;
        DateTimeOffset now = clock.GetUtcNow();
        return await LiveQuestions.Of(db, culture, stack)
            .Join(db.ReviewStates.Where(r => r.UserId == userId && r.DueAt > now), q => q.Id, r => r.QuestionId, (_, r) => (DateTimeOffset?)r.DueAt)
            .MinAsync(ct);
    }

    private async Task<IReadOnlyList<MockHistoryDto>> GetRecentMocksAsync(UserId user, StackKey stack, CancellationToken ct)
    {
        string userId = user.Value;
        string stackValue = stack.Value;
        var rows = await db.MockSessions
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.StackKey == stackValue)
            .OrderByDescending(m => m.FinishedAt)
            .ThenByDescending(m => m.Id)
            .Take(RecentMockCount)
            .Select(m => new { m.Id, m.Level, m.QuestionCount, m.FinishedAt, m.Summary })
            .ToListAsync(ct);

        return [.. rows.Select(r =>
        {
            IReadOnlyList<MockTopicDocument> topics = MockSummaryDocument.Parse(r.Summary)?.Topics ?? [];
            return new MockHistoryDto(
                r.Id,
                LevelKeys.ToKey(r.Level),
                r.QuestionCount,
                r.FinishedAt,
                topics.Sum(t => t.Knew),
                topics.Sum(t => t.Partial),
                topics.Sum(t => t.DidntKnow),
                topics.Sum(t => t.Unanswered));
        })];
    }

    /// <summary>
    /// Counts consecutive local answer days ending today or yesterday. The day boundary uses the offset the user's client
    /// sent with its latest write, so an evening learner keeps the streak whatever the time zone; it is UTC until the first
    /// write. Days arrive newest first and reading stops at the first gap.
    /// </summary>
    private async Task<int> GetStreakAsync(UserId user, CancellationToken ct)
    {
        string userId = user.Value;
        int offset = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => (int)u.UtcOffsetMinutes)
            .FirstOrDefaultAsync(ct);

        DateOnly today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime.AddMinutes(offset));
        StreakAccumulator streak = new(today);
        // The base query only de-duplicates days; the ordering is applied on top so the result is a stream the client can cut short.
        IQueryable<DateOnly> days = db.Database
            .SqlQuery<DateOnly>($"""
                SELECT DISTINCT ((a.answered_at + {offset} * interval '1 minute') AT TIME ZONE 'UTC')::date AS "Value"
                FROM answers AS a
                WHERE a.user_id = {userId}
                """)
            .OrderByDescending(d => d);

        await foreach (DateOnly day in days.AsAsyncEnumerable().WithCancellation(ct))
        {
            if (!streak.Add(day))
            {
                break;
            }
        }

        return streak.Days;
    }
}
