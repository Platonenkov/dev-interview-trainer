using Trainer.Domain.Ids;
using Trainer.Domain.Users;

namespace Trainer.Application.Mock;

public interface IMockSessionService
{
    /// <summary>
    /// Creates or replaces the summary of the user's mock session, idempotent by <paramref name="id"/>.
    /// <c>StartedAt</c> and <c>FinishedAt</c> are judged by <c>AnswerTimePolicy</c>: a rejected time gives
    /// <see cref="MockSaveResult.Invalid"/>, a time up to the tolerance ahead is stored as the server's now.
    /// </summary>
    Task<MockSaveResult> SaveAsync(UserId userId, MockSessionId id, MockSummary summary, CancellationToken ct);
}
