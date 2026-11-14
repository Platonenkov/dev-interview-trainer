using Trainer.Domain.Grading;

namespace Trainer.Infrastructure.Persistence.Entities;

/// <summary>Review schedule of one question for one user; see <c>Trainer.Domain.Scheduling.ReviewState</c>.</summary>
internal sealed class ReviewStateEntity
{
    public required string UserId { get; set; }
    public required string QuestionId { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public int IntervalDays { get; set; }
    public decimal Ease { get; set; }
    public int Repetitions { get; set; }
    public int Lapses { get; set; }
    public Grade LastGrade { get; set; }
    public DateTimeOffset LastAnsweredAt { get; set; }

    /// <summary>
    /// PostgreSQL's <c>xmin</c> system column: the "version" of spec 8.6. The database changes it on every write, so
    /// there is nothing to maintain, and EF Core uses it as the optimistic concurrency token.
    /// </summary>
    public uint Version { get; set; }
}
