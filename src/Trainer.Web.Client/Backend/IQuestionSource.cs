using Trainer.Contracts;
using Trainer.Web.Client.Api;

namespace Trainer.Web.Client.Backend;

/// <summary>
/// Where a session gets its questions by id. A backend that serves everything in one response hands out questions it
/// already holds; a static backend loads each one on demand, so a session never waits for questions it has not reached.
/// </summary>
public interface IQuestionSource
{
    /// <summary>Returns the question, loading it if necessary; a failed load comes back as a result, never as an exception.</summary>
    ValueTask<ApiResult<QuestionDto>> GetAsync(string questionId, CancellationToken ct);

    /// <summary>Starts loading a question the session will most likely ask for next; fire and forget.</summary>
    void Prefetch(string questionId);
}
