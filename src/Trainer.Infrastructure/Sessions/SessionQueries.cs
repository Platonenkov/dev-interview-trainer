using Microsoft.EntityFrameworkCore;
using Trainer.Application.Feedback;
using Trainer.Application.Sessions;
using Trainer.Contracts;
using Trainer.Domain.Feedback;
using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;
using Trainer.Domain.Sessions;
using Trainer.Domain.Users;
using Trainer.Infrastructure.Persistence;
using Trainer.Infrastructure.Persistence.Entities;
using Trainer.Infrastructure.Queries;

namespace Trainer.Infrastructure.Sessions;

/// <summary>
/// Session queries. Each one runs a constant number of commands whatever the session size: the pool count, one query that
/// returns the questions with their locale and payload, the topic titles and, for a signed-in user, the user's ratings.
/// </summary>
internal sealed class SessionQueries(TrainerDbContext db, TimeProvider clock, MockComposer composer, IQuestionFeedbackQueries feedback) : ISessionQueries
{
    // A parameter-driven status switch keeps one query shape for every status; see PracticePool.
    private const int StatusAll = 0;
    private const int StatusNewSignedIn = 1;
    private const int StatusNewAnonymous = 2;
    private const int StatusDue = 3;
    private const int StatusNone = 4;

    public async Task<SessionDto> GetPracticeAsync(PracticeCriteria criteria, UserId? user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        EnsureCount(criteria.Count, SessionLimits.MinPracticeCount);
        if (criteria.Exclude.Count > SessionLimits.MaxExcludedQuestions)
        {
            throw new ArgumentException($"At most {SessionLimits.MaxExcludedQuestions} excluded questions are allowed.", nameof(criteria));
        }

        IQueryable<SessionCandidate> pool = PracticePool(criteria, user);
        string first = criteria.First?.Value ?? string.Empty;
        int poolSize = await pool.CountAsync(ct);
        List<SessionRow> rows = await pool
            .OrderBy(c => c.Question.Id == first ? 0 : 1)
            .ThenBy(_ => EF.Functions.Random())
            .Take(criteria.Count)
            .Select(SessionRows.Project)
            .ToListAsync(ct);

        IReadOnlyList<QuestionDto> questions = await ToDtosAsync(rows, criteria.Culture, criteria.Stack, ct);
        return new SessionDto(questions, poolSize, await MyRatingsAsync(user, questions, ct));
    }

    public async Task<MockSessionDto> GetMockAsync(MockCriteria criteria, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        EnsureCount(criteria.Count, SessionLimits.MinMockCount);

        Level level = criteria.Level;
        // Junior has no lower level; level 0 matches no question.
        Level below = (Level)((int)level - 1);
        string[] topics = [.. criteria.Topics.Select(t => t.Value)];
        bool anyTopics = topics.Length > 0;
        var candidates = await LiveQuestions.Of(db, criteria.Culture, criteria.Stack)
            .Where(q => (q.Level == level || q.Level == below) && (!anyTopics || topics.Contains(q.TopicKey)))
            .Select(q => new { q.Id, q.Level })
            .ToListAsync(ct);

        QuestionId[] atLevel = [.. candidates.Where(c => c.Level == level).Select(c => new QuestionId(c.Id))];
        QuestionId[] lower = [.. candidates.Where(c => c.Level == below).Select(c => new QuestionId(c.Id))];
        IReadOnlyList<QuestionId> chosen = composer.Compose(level, atLevel, lower, criteria.Count);

        string[] chosenIds = [.. chosen.Select(c => c.Value)];
        List<SessionRow> rows = chosenIds.Length == 0 ? [] : await SessionCandidates(LiveQuestions.Of(db, criteria.Culture, criteria.Stack).Where(q => chosenIds.Contains(q.Id)), criteria.Culture)
            .Select(SessionRows.Project)
            .ToListAsync(ct);

        // The composer's order is the session order; the database returns the rows in any order.
        Dictionary<string, int> position = chosenIds.Select((id, index) => (id, index)).ToDictionary(p => p.id, p => p.index, StringComparer.Ordinal);
        rows.Sort((a, b) => position[a.Id].CompareTo(position[b.Id]));

        int poolSize = atLevel.Length + lower.Length;
        IReadOnlyList<QuestionDto> questions = await ToDtosAsync(rows, criteria.Culture, criteria.Stack, ct);
        return new MockSessionDto(questions, poolSize, MockComposer.RepeatsLikely(poolSize, criteria.Count), MockComposer.SuggestedCount(poolSize));
    }

    public async Task<SessionDto> GetReviewAsync(ReviewCriteria criteria, UserId user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        EnsureCount(criteria.Count, SessionLimits.MinReviewCount);

        string userId = user.Value;
        DateTimeOffset now = clock.GetUtcNow();
        string culture = criteria.Culture.Value;
        IQueryable<SessionCandidate> due = db.ReviewStates
            .AsNoTracking()
            .Where(r => r.UserId == userId && r.DueAt <= now)
            .Join(LiveQuestions.Of(db, criteria.Culture, criteria.Stack), r => r.QuestionId, q => q.Id, (r, q) => new { r.DueAt, Question = q })
            .Join(
                db.QuestionLocales.Where(l => l.Culture == culture),
                x => x.Question.Id,
                l => l.QuestionId,
                (x, l) => new SessionCandidate { Question = x.Question, Locale = l, DueAt = x.DueAt });

        int poolSize = await due.CountAsync(ct);
        List<SessionRow> rows = await due
            .OrderBy(c => c.DueAt)
            .ThenBy(c => c.Question.Id)
            .Take(criteria.Count)
            .Select(SessionRows.Project)
            .ToListAsync(ct);

        IReadOnlyList<QuestionDto> questions = await ToDtosAsync(rows, criteria.Culture, criteria.Stack, ct);
        return new SessionDto(questions, poolSize, await MyRatingsAsync(user, questions, ct));
    }

    /// <summary>The signed-in user's ratings among the session's questions as wire keys; null for an anonymous session.</summary>
    private async Task<IReadOnlyDictionary<string, string>?> MyRatingsAsync(UserId? user, IReadOnlyList<QuestionDto> questions, CancellationToken ct)
    {
        if (user is not { } signedIn)
        {
            return null;
        }

        if (questions.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        QuestionId[] ids = [.. questions.Select(q => new QuestionId(q.Id))];
        IReadOnlyDictionary<QuestionId, QuestionRating> ratings = await feedback.GetMyRatingsAsync(signedIn, ids, ct);
        return ratings.ToDictionary(r => r.Key.Value, r => QuestionRatingKeys.ToKey(r.Value), StringComparer.Ordinal);
    }

    /// <summary>
    /// The practice pool: live questions of the stack that match the filters and the status, plus the first question when it
    /// is live. Everything is one predicate so the first question needs no extra query and counts toward the pool.
    /// </summary>
    private IQueryable<SessionCandidate> PracticePool(PracticeCriteria criteria, UserId? user)
    {
        Level[] levels = [.. criteria.Levels];
        string[] topics = [.. criteria.Topics.Select(t => t.Value)];
        QuestionType[] types = [.. criteria.Types];
        string[] excluded = [.. criteria.Exclude.Select(e => e.Value)];
        bool anyLevels = levels.Length > 0;
        bool anyTopics = topics.Length > 0;
        bool anyTypes = types.Length > 0;
        string first = criteria.First?.Value ?? string.Empty;
        string userId = user?.Value ?? string.Empty;
        DateTimeOffset now = clock.GetUtcNow();
        int status = StatusOf(criteria.Status, user is not null);

        IQueryable<QuestionEntity> questions = LiveQuestions.Of(db, criteria.Culture, criteria.Stack)
            .Where(q => q.Id == first
                || ((!anyLevels || levels.Contains(q.Level))
                    && (!anyTopics || topics.Contains(q.TopicKey))
                    && (!anyTypes || types.Contains(q.Type))
                    && !excluded.Contains(q.Id)
                    && (status == StatusAll
                        || (status == StatusNewSignedIn && !db.ReviewStates.Any(r => r.UserId == userId && r.QuestionId == q.Id))
                        || status == StatusNewAnonymous
                        || (status == StatusDue && db.ReviewStates.Any(r => r.UserId == userId && r.QuestionId == q.Id && r.DueAt <= now)))));
        return SessionCandidates(questions, criteria.Culture);
    }

    private IQueryable<SessionCandidate> SessionCandidates(IQueryable<QuestionEntity> questions, CultureKey culture) =>
        SessionRows.Candidates(db, questions, culture);

    private async Task<IReadOnlyList<QuestionDto>> ToDtosAsync(
        List<SessionRow> rows, CultureKey culture, StackKey stack, CancellationToken ct)
    {
        Dictionary<string, string> titles = await LiveQuestions.TopicTitlesAsync(db, culture, stack, ct);
        return [.. rows.Select(r => SessionRows.ToDto(r, titles))];
    }

    private static int StatusOf(PracticeStatus status, bool signedIn) => status switch
    {
        PracticeStatus.All => StatusAll,
        PracticeStatus.New => signedIn ? StatusNewSignedIn : StatusNewAnonymous,
        PracticeStatus.Due => signedIn ? StatusDue : StatusNone,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    private static void EnsureCount(int count, int min)
    {
        if (count < min || count > SessionLimits.MaxCount)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, $"The count must be between {min} and {SessionLimits.MaxCount}.");
        }
    }
}
