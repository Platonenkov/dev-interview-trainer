using Trainer.Domain.Grading;

namespace Trainer.Domain.Scheduling;

/// <summary>Pure function from the current review state and one answer to the next state.</summary>
public interface IReviewScheduler
{
    /// <summary>
    /// Applies an answer. Returns <paramref name="current"/> itself when the answer is not newer than the last applied
    /// one, so a delayed or duplicated answer never rewinds the schedule.
    /// </summary>
    ReviewState Apply(ReviewState? current, Grade grade, DateTimeOffset answeredAt);
}
