using Trainer.Domain.Feedback;

namespace Trainer.Infrastructure.Persistence.Entities;

/// <summary>One user's current rating of one question. Written only by an upsert in <c>QuestionFeedbackService</c>.</summary>
internal sealed class QuestionRatingEntity
{
    public required string UserId { get; set; }
    public required string QuestionId { get; set; }
    public QuestionRating Value { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
