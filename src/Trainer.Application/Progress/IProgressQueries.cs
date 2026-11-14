using Trainer.Contracts;
using Trainer.Domain.Keys;
using Trainer.Domain.Questions;
using Trainer.Domain.Users;

namespace Trainer.Application.Progress;

/// <summary>
/// Read side of the signed-in user's learning progress. Everything counts live questions only (not retired, with a
/// locale in the culture), the same set the public catalog shows.
/// </summary>
public interface IProgressQueries
{
    /// <summary>The statuses of the answered questions among <paramref name="ids"/>; a question without a review state is absent.</summary>
    Task<IReadOnlyDictionary<QuestionId, QuestionStatus>> GetStatusesAsync(
        UserId user, IReadOnlyCollection<QuestionId> ids, CancellationToken ct);

    Task<int> GetDueCountAsync(UserId user, CultureKey culture, StackKey stack, CancellationToken ct);

    /// <summary>
    /// <see cref="ProfileDto.TotalAnswered"/> is the number of live questions the user has a review state for, so it equals
    /// the sum of the per-topic <see cref="TopicProgressDto.Answered"/> counts. The streak counts local days by the UTC
    /// offset of the user's latest write.
    /// </summary>
    Task<ProfileDto> GetProfileAsync(UserId user, CultureKey culture, StackKey stack, CancellationToken ct);

    /// <summary>The slice of the profile an island needs at startup.</summary>
    /// <exception cref="InvalidOperationException">The user does not exist.</exception>
    Task<MeDto> GetMeAsync(UserId user, CultureKey culture, StackKey stack, CancellationToken ct);
}
