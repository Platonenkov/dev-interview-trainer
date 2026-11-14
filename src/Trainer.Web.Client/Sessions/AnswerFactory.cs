using Trainer.Contracts;
using Trainer.Domain.Grading;

namespace Trainer.Web.Client.Sessions;

/// <summary>
/// Turns a graded attempt into the outbox entry (spec 8.8). The id and <c>answeredAt</c> are the attempt's own, so
/// creating the entry again yields the same answer, which the outbox and the server deduplicate.
/// </summary>
public sealed class AnswerFactory
{
    /// <param name="mockSessionId">Required for <see cref="AnswerMode.Mock"/>, null otherwise.</param>
    /// <exception cref="InvalidOperationException">The attempt is not graded (unanswered or skipped).</exception>
    /// <exception cref="ArgumentException">The mock session id does not match the mode.</exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "An injected service, so Mock and Practice share one way of building answers.")]
    public AnswerDto Create(QuestionAttempt attempt, AnswerMode mode, string? mockSessionId)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.Grade is not { } grade || attempt.AnsweredAt is not { } answeredAt)
        {
            throw new InvalidOperationException("Only a graded attempt is an answer.");
        }

        if ((mode == AnswerMode.Mock) != (mockSessionId is not null))
        {
            throw new ArgumentException("A mock answer needs its session id; other modes have none.", nameof(mockSessionId));
        }

        return new AnswerDto(
            attempt.ClientAnswerId,
            attempt.Question.Id,
            GradeKeys.ToKey(grade),
            attempt.UsedHint,
            AnswerModeKeys.ToKey(mode),
            mockSessionId,
            answeredAt);
    }
}
