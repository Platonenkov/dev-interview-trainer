using Trainer.Domain.Grading;

namespace Trainer.Infrastructure.Persistence.Entities;

/// <summary>One recorded answer. Append-only: rows are inserted by <c>AnswerRecorder</c> and never updated.</summary>
internal sealed class AnswerEntity
{
    /// <summary>Server-generated ULID.</summary>
    public required string Id { get; set; }

    /// <summary>Client-generated ULID; unique, so outbox retries are idempotent.</summary>
    public required string ClientAnswerId { get; set; }

    public required string UserId { get; set; }
    public required string QuestionId { get; set; }
    public Grade Grade { get; set; }
    public bool UsedHint { get; set; }
    public AnswerMode Mode { get; set; }

    /// <summary>No foreign key: answers usually arrive before the mock summary, and anonymous mocks never send one.</summary>
    public string? MockSessionId { get; set; }

    /// <summary>The client's time after <c>AnswerTimePolicy</c> (ADR 0005); scheduling uses this value.</summary>
    public DateTimeOffset AnsweredAt { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }
}
