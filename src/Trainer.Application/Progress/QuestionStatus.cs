using Trainer.Domain.Grading;

namespace Trainer.Application.Progress;

/// <summary>
/// One signed-in user's state of one answered question. <paramref name="IsDue"/> is true when
/// <paramref name="DueAt"/> is not after the moment the status was read.
/// </summary>
public sealed record QuestionStatus(Grade LastGrade, DateTimeOffset DueAt, bool IsDue);
