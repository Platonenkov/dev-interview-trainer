using Microsoft.EntityFrameworkCore;
using Trainer.Application.Answers;
using Trainer.Application.Mock;
using Trainer.Domain.Ids;
using Trainer.Domain.Users;
using Trainer.Infrastructure.Persistence;

namespace Trainer.Infrastructure.Mock;

internal sealed class MockSessionService(TrainerDbContext db, TimeProvider time) : IMockSessionService
{
    public async Task<MockSaveResult> SaveAsync(UserId userId, MockSessionId id, MockSummary summary, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(summary);

        DateTimeOffset now = time.GetUtcNow();
        if (!TryEffective(summary.StartedAt, now, out DateTimeOffset startedAt)
            || !TryEffective(summary.FinishedAt, now, out DateTimeOffset finishedAt)
            || finishedAt < startedAt)
        {
            return MockSaveResult.Invalid;
        }

        SessionRow row = new(
            id.Value,
            userId.Value,
            summary.Stack.Value,
            (short)summary.Level,
            summary.QuestionCount,
            summary.Timed,
            startedAt,
            finishedAt,
            MockSummaryDocument.Serialize(summary.Topics));

        if (await UpdateOwnAsync(row, ct))
        {
            return MockSaveResult.Updated;
        }

        if (await InsertNewAsync(row, ct))
        {
            return MockSaveResult.Created;
        }

        // The id exists but was not updated a moment ago: either a parallel save of the same session inserted it in
        // between, or it belongs to another user. Only the first case updates; the second stays indistinguishable from
        // a missing session, so nothing about the other user's session is revealed.
        return await UpdateOwnAsync(row, ct) ? MockSaveResult.Updated : MockSaveResult.NotFound;
    }

    private async Task<bool> UpdateOwnAsync(SessionRow row, CancellationToken ct)
    {
        int affected = await db.Database.ExecuteSqlAsync($"""
            UPDATE mock_sessions
            SET stack_key = {row.StackKey}, level = {row.Level}, question_count = {row.QuestionCount}, timed = {row.Timed},
                started_at = {row.StartedAt}, finished_at = {row.FinishedAt}, summary = CAST({row.Summary} AS jsonb)
            WHERE id = {row.Id} AND user_id = {row.UserId}
            """, ct);
        return affected > 0;
    }

    private async Task<bool> InsertNewAsync(SessionRow row, CancellationToken ct)
    {
        int affected = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO mock_sessions (id, user_id, stack_key, level, question_count, timed, started_at, finished_at, summary)
            VALUES ({row.Id}, {row.UserId}, {row.StackKey}, {row.Level}, {row.QuestionCount}, {row.Timed},
                    {row.StartedAt}, {row.FinishedAt}, CAST({row.Summary} AS jsonb))
            ON CONFLICT (id) DO NOTHING
            """, ct);
        return affected > 0;
    }

    private static bool TryEffective(DateTimeOffset reported, DateTimeOffset now, out DateTimeOffset effective)
    {
        if (AnswerTimePolicy.Judge(reported, now) is TimeVerdict.TooOld or TimeVerdict.InFuture)
        {
            effective = default;
            return false;
        }

        effective = StoredTime.Normalize(AnswerTimePolicy.Effective(reported, now));
        return true;
    }

    private sealed record SessionRow(
        string Id,
        string UserId,
        string StackKey,
        short Level,
        int QuestionCount,
        bool Timed,
        DateTimeOffset StartedAt,
        DateTimeOffset FinishedAt,
        string Summary);
}
