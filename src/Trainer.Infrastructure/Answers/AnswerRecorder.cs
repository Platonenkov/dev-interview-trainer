using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using Trainer.Application.Answers;
using Trainer.Domain.Grading;
using Trainer.Domain.Ids;
using Trainer.Domain.Scheduling;
using Trainer.Domain.Users;
using Trainer.Infrastructure.Persistence;
using Trainer.Infrastructure.Persistence.Entities;

namespace Trainer.Infrastructure.Answers;

/// <summary>
/// Records answer batches (spec 8.8, ADR 0005). One transaction per batch, serialised per user by an advisory lock;
/// <c>review_states.xmin</c> still guards every schedule write, and a conflict retries the whole batch.
/// </summary>
internal sealed partial class AnswerRecorder(
    TrainerDbContext db,
    IReviewScheduler scheduler,
    UlidGenerator ids,
    TimeProvider time,
    ILogger<AnswerRecorder> logger) : IAnswerRecorder
{
    private const int MaxRetries = 3;
    internal const string LockPrefix = "dit-answers:";

    public async Task<AnswerBatchResult> RecordAsync(
        UserId userId, IReadOnlyList<ReportedAnswer> answers, short? clientUtcOffsetMinutes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(answers);
        if (answers.Count > IAnswerRecorder.MaxBatchSize)
        {
            throw new ArgumentException($"A batch holds at most {IAnswerRecorder.MaxBatchSize} answers.", nameof(answers));
        }

        List<ReportedAnswer> distinct = FirstOfEachId(answers);
        short? offset = clientUtcOffsetMinutes is { } minutes && Math.Abs((int)minutes) <= IAnswerRecorder.MaxUtcOffsetMinutes
            ? minutes
            : null;

        for (int retry = 0; ; retry++)
        {
            try
            {
                BatchOutcome outcome = await RecordOnceAsync(userId, distinct, offset, ct);
                // Logged only after the commit, so a retried attempt does not log twice.
                foreach (string clientAnswerId in outcome.ChangedPayloadIds)
                {
                    LogChangedPayload(logger, clientAnswerId);
                }

                return outcome.Result;
            }
            catch (Exception exception) when (IsWriteConflict(exception) && retry < MaxRetries)
            {
                // The transaction is already rolled back; the next attempt rereads everything. The tracker is cleared
                // only here, so a successful batch leaves entities the caller tracked on the same context alone.
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<BatchOutcome> RecordOnceAsync(
        UserId userId, List<ReportedAnswer> answers, short? offset, CancellationToken ct)
    {
        DateTimeOffset now = StoredTime.Normalize(time.GetUtcNow());
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(ct);

        // Serialises batches of one user, so a retried batch never races its original. A hash collision only
        // serialises two users' batches.
        string lockKey = LockPrefix + userId.Value;
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);

        Dictionary<string, StoredAnswer> stored = await LoadStoredAsync(answers, ct);
        Dictionary<string, string> rejections = new(StringComparer.Ordinal);
        List<string> changedPayloadIds = [];
        List<TimedAnswer> timely = [];
        foreach (ReportedAnswer answer in answers)
        {
            string id = answer.ClientAnswerId.Value;
            if (stored.TryGetValue(id, out StoredAnswer? existing))
            {
                if (!existing.Matches(userId, answer))
                {
                    changedPayloadIds.Add(id);
                }

                continue;
            }

            switch (AnswerTimePolicy.Judge(answer.AnsweredAt, now))
            {
                case TimeVerdict.TooOld:
                    rejections[id] = AnswerRejections.AnsweredTooOld;
                    break;
                case TimeVerdict.InFuture:
                    rejections[id] = AnswerRejections.AnsweredInFuture;
                    break;
                default:
                    timely.Add(new TimedAnswer(answer, StoredTime.Normalize(AnswerTimePolicy.Effective(answer.AnsweredAt, now))));
                    break;
            }
        }

        List<TimedAnswer> fresh = await RejectUnavailableQuestionsAsync(timely, rejections, ct);

        if (offset is { } minutes)
        {
            await db.Users
                .Where(u => u.Id == userId.Value)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.UtcOffsetMinutes, minutes), ct);
        }

        foreach (TimedAnswer item in fresh)
        {
            db.Answers.Add(ToEntity(userId, item, now));
        }

        await ScheduleAsync(userId, fresh, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        string[] accepted = [.. answers.Select(a => a.ClientAnswerId.Value).Where(id => !rejections.ContainsKey(id))];
        RejectedAnswer[] rejected = [.. answers
            .Where(a => rejections.ContainsKey(a.ClientAnswerId.Value))
            .Select(a => new RejectedAnswer(a.ClientAnswerId.Value, rejections[a.ClientAnswerId.Value]))];
        return new BatchOutcome(new AnswerBatchResult(accepted, rejected), changedPayloadIds);
    }

    private async Task<Dictionary<string, StoredAnswer>> LoadStoredAsync(List<ReportedAnswer> answers, CancellationToken ct)
    {
        string[] clientIds = [.. answers.Select(a => a.ClientAnswerId.Value)];
        return await db.Answers
            .AsNoTracking()
            .Where(a => clientIds.Contains(a.ClientAnswerId))
            .Select(a => new StoredAnswer(
                a.ClientAnswerId, a.UserId, a.QuestionId, a.Grade, a.UsedHint, a.Mode, a.MockSessionId, a.AnsweredAt))
            .ToDictionaryAsync(a => a.ClientAnswerId, StringComparer.Ordinal, ct);
    }

    /// <summary>
    /// Rejects answers whose question does not exist or is retired, and returns the rest. A question counts as retired
    /// when it, its topic or its stack is retired: such a question has left the catalog, the same rule as the public pages.
    /// </summary>
    private async Task<List<TimedAnswer>> RejectUnavailableQuestionsAsync(
        List<TimedAnswer> answers, Dictionary<string, string> rejections, CancellationToken ct)
    {
        if (answers.Count == 0)
        {
            return answers;
        }

        string[] questionIds = [.. answers.Select(a => a.Answer.QuestionId.Value).Distinct(StringComparer.Ordinal)];
        Dictionary<string, bool> retiredById = await db.Questions
            .AsNoTracking()
            .Where(q => questionIds.Contains(q.Id))
            .Select(q => new
            {
                q.Id,
                Retired = q.RetiredAt != null
                    || db.Stacks.Any(s => s.Key == q.StackKey && s.RetiredAt != null)
                    || db.Topics.Any(t => t.StackKey == q.StackKey && t.Key == q.TopicKey && t.RetiredAt != null),
            })
            .ToDictionaryAsync(q => q.Id, q => q.Retired, StringComparer.Ordinal, ct);

        List<TimedAnswer> available = new(answers.Count);
        foreach (TimedAnswer item in answers)
        {
            string id = item.Answer.ClientAnswerId.Value;
            if (!retiredById.TryGetValue(item.Answer.QuestionId.Value, out bool retired))
            {
                rejections[id] = AnswerRejections.QuestionUnknown;
            }
            else if (retired)
            {
                rejections[id] = AnswerRejections.QuestionRetired;
            }
            else
            {
                available.Add(item);
            }
        }

        return available;
    }

    private async Task ScheduleAsync(UserId userId, List<TimedAnswer> answers, CancellationToken ct)
    {
        if (answers.Count == 0)
        {
            return;
        }

        string user = userId.Value;
        string[] questionIds = [.. answers.Select(a => a.Answer.QuestionId.Value).Distinct(StringComparer.Ordinal)];
        Dictionary<string, ReviewStateEntity> states = await db.ReviewStates
            .Where(r => r.UserId == user && questionIds.Contains(r.QuestionId))
            .ToDictionaryAsync(r => r.QuestionId, StringComparer.Ordinal, ct);

        // The scheduler ignores an answer that is not newer than the last applied one, so the order decides which of
        // two answers with the same time counts. Answers clamped to the server's now share a stored time, so the time
        // the client reported comes next (the one given first wins, as with any two answers), and the client id makes
        // the rest deterministic (ADR 0005).
        IEnumerable<TimedAnswer> ordered = answers
            .OrderBy(a => a.AnsweredAt)
            .ThenBy(a => a.Answer.AnsweredAt)
            .ThenBy(a => a.Answer.ClientAnswerId.Value, StringComparer.Ordinal);
        foreach (TimedAnswer item in ordered)
        {
            string questionId = item.Answer.QuestionId.Value;
            ReviewStateEntity? entity = states.GetValueOrDefault(questionId);
            ReviewState? current = entity is null ? null : ToDomain(entity);
            ReviewState next = scheduler.Apply(current, item.Answer.Grade, item.AnsweredAt);
            if (ReferenceEquals(next, current))
            {
                continue;
            }

            if (entity is null)
            {
                entity = new ReviewStateEntity { UserId = user, QuestionId = questionId };
                db.ReviewStates.Add(entity);
                states[questionId] = entity;
            }

            Apply(next, entity);
        }
    }

    private AnswerEntity ToEntity(UserId userId, TimedAnswer item, DateTimeOffset now) => new()
    {
        Id = ids.Next(),
        ClientAnswerId = item.Answer.ClientAnswerId.Value,
        UserId = userId.Value,
        QuestionId = item.Answer.QuestionId.Value,
        Grade = item.Answer.Grade,
        UsedHint = item.Answer.UsedHint,
        Mode = item.Answer.Mode,
        MockSessionId = item.Answer.MockSessionId?.Value,
        AnsweredAt = item.AnsweredAt,
        ReceivedAt = now,
    };

    private static ReviewState ToDomain(ReviewStateEntity entity) => new(
        entity.DueAt,
        entity.IntervalDays,
        entity.Ease,
        entity.Repetitions,
        entity.Lapses,
        entity.LastGrade,
        entity.LastAnsweredAt);

    private static void Apply(ReviewState state, ReviewStateEntity entity)
    {
        entity.DueAt = state.DueAt;
        entity.IntervalDays = state.IntervalDays;
        entity.Ease = state.Ease;
        entity.Repetitions = state.Repetitions;
        entity.Lapses = state.Lapses;
        entity.LastGrade = state.LastGrade;
        entity.LastAnsweredAt = state.LastAnsweredAt;
    }

    private static List<ReportedAnswer> FirstOfEachId(IReadOnlyList<ReportedAnswer> answers)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<ReportedAnswer> result = new(answers.Count);
        foreach (ReportedAnswer answer in answers)
        {
            if (seen.Add(answer.ClientAnswerId.Value))
            {
                result.Add(answer);
            }
        }

        return result;
    }

    /// <summary>
    /// A stale <c>xmin</c> on a review state, a unique violation from a parallel writer (another user's batch with the
    /// same client id, or a review state created by a writer that does not take the lock), or a deadlock or
    /// serialization failure PostgreSQL resolved by aborting this transaction (two users' batches that reuse each
    /// other's client ids in opposite order). No retrying execution strategy is configured, so this method is the only
    /// retry; Npgsql's default (non-retrying) strategy still wraps the transient states (40P01, 40001) in an
    /// <see cref="InvalidOperationException"/> around a <see cref="DbUpdateException"/> around the
    /// <see cref="PostgresException"/>, so the whole inner chain is searched.
    /// </summary>
    private static bool IsWriteConflict(Exception exception)
    {
        if (exception is DbUpdateConcurrencyException)
        {
            return true;
        }

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure })
            {
                return true;
            }
        }

        return false;
    }

    // Only the id: the payload is user data and the id is enough to find the stored row.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Answer {ClientAnswerId} was reported again with a different payload; the stored answer is kept.")]
    private static partial void LogChangedPayload(ILogger logger, string clientAnswerId);

    private sealed record TimedAnswer(ReportedAnswer Answer, DateTimeOffset AnsweredAt);

    private sealed record BatchOutcome(AnswerBatchResult Result, IReadOnlyList<string> ChangedPayloadIds);

    private sealed record StoredAnswer(
        string ClientAnswerId,
        string UserId,
        string QuestionId,
        Grade Grade,
        bool UsedHint,
        AnswerMode Mode,
        string? MockSessionId,
        DateTimeOffset AnsweredAt)
    {
        /// <summary>
        /// True when the report repeats the stored answer. A stored time equal to the reported one, or up to the
        /// tolerance before it (the original was clamped to the server's now), counts as the same time.
        /// </summary>
        public bool Matches(UserId userId, ReportedAnswer answer)
        {
            DateTimeOffset reported = StoredTime.Normalize(answer.AnsweredAt);
            bool sameTime = reported == AnsweredAt
                || (reported > AnsweredAt && reported <= AnsweredAt + AnswerTimePolicy.FutureTolerance);
            return sameTime
                && string.Equals(UserId, userId.Value, StringComparison.Ordinal)
                && string.Equals(QuestionId, answer.QuestionId.Value, StringComparison.Ordinal)
                && Grade == answer.Grade
                && UsedHint == answer.UsedHint
                && Mode == answer.Mode
                && string.Equals(MockSessionId, answer.MockSessionId?.Value, StringComparison.Ordinal);
        }
    }
}
