using Trainer.Domain.Feedback;

namespace Trainer.Infrastructure.Persistence.Entities;

/// <summary>A user's report about a question. Open until an admin resolves it; resolved rows stay as history.</summary>
internal sealed class QuestionReportEntity
{
    /// <summary>Server-generated ULID.</summary>
    public required string Id { get; set; }

    public required string UserId { get; set; }
    public required string QuestionId { get; set; }

    /// <summary>The culture of the page the report was filed from; the admin page links back to that version.</summary>
    public required string Culture { get; set; }

    public ReportReason Reason { get; set; }

    /// <summary>User text, normalised by <c>ReportComment</c>. Never logged.</summary>
    public string? Comment { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
}
