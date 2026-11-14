using Trainer.Contracts;
using Trainer.Domain.Users;

namespace Trainer.Application.Sessions;

/// <summary>
/// Loads a whole session up front, so answering works offline once it has started. Only live questions that have a locale
/// for the culture are offered. Each method throws <see cref="ArgumentOutOfRangeException"/> for a count outside
/// <see cref="SessionLimits"/>.
/// </summary>
public interface ISessionQueries
{
    /// <summary>
    /// Random questions matching the criteria. <c>PoolSize</c> counts the matches before <c>Count</c> is applied. A <c>New</c>
    /// or <c>Due</c> query needs <paramref name="user"/> for the review states; anonymous <c>New</c> uses <c>Exclude</c>.
    /// An anonymous <c>Due</c> query matches nothing except <see cref="PracticeCriteria.First"/>, which is outside every
    /// filter; the API refuses anonymous <c>due</c> before it gets here.
    /// </summary>
    Task<SessionDto> GetPracticeAsync(PracticeCriteria criteria, UserId? user, CancellationToken ct);

    /// <summary>A shuffled mix of the level and the level below (see <see cref="MockComposer"/>); a pool under 5 yields all of it.</summary>
    Task<MockSessionDto> GetMockAsync(MockCriteria criteria, CancellationToken ct);

    /// <summary>The user's due questions, most overdue first, without retired questions or those lacking the culture's locale.</summary>
    Task<SessionDto> GetReviewAsync(ReviewCriteria criteria, UserId user, CancellationToken ct);
}
