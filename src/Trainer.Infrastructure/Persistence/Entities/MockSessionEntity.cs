using Trainer.Domain.Levels;

namespace Trainer.Infrastructure.Persistence.Entities;

/// <summary>Summary of one finished mock interview, written by <c>MockSessionService</c>.</summary>
internal sealed class MockSessionEntity
{
    /// <summary>Client-generated ULID.</summary>
    public required string Id { get; set; }

    public required string UserId { get; set; }
    public required string StackKey { get; set; }
    public Level Level { get; set; }
    public int QuestionCount { get; set; }
    public bool Timed { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }

    /// <summary>Per-topic results as JSON, see <c>MockSummaryDocument</c>.</summary>
    public string Summary { get; set; } = "{}";
}
