using Trainer.Contracts;
using Trainer.Web.Client.Api;

namespace Trainer.Web.Client.Backend;

/// <summary>
/// Everything an island needs from the data side, so the same islands run on the server edition (the <c>/api</c>) and
/// on the static one. Failures come back as <see cref="ApiResult{T}"/>; nothing here throws for network problems.
/// </summary>
public interface IIslandBackend
{
    /// <summary>Whether this visitor has progress (streak, review queue, per topic stats) and the pages that show it.</summary>
    bool KeepsProgress(IslandBootstrapDto bootstrap);

    /// <summary>The outbox key the visitor's answers are written under.</summary>
    string OutboxKey(IslandBootstrapDto bootstrap);

    /// <summary>
    /// Whether answers still waiting in the outbox must be removed from a Review queue: the server counts a question as
    /// due until it has received the answer.
    /// </summary>
    bool FiltersPendingAnswers { get; }

    /// <summary>Whether an anonymous "new" session names the questions answered in this browser in <c>Exclude</c>.</summary>
    bool SendsAnsweredAsExclude { get; }

    Task<IslandProgress?> GetProgressAsync(IslandBootstrapDto bootstrap, CancellationToken ct);

    Task<ApiResult<SessionPlan>> PracticeAsync(IslandBootstrapDto bootstrap, PracticeSessionRequest request, CancellationToken ct);

    Task<ApiResult<MockSessionDto>> MockAsync(IslandBootstrapDto bootstrap, MockSessionRequest request, CancellationToken ct);

    Task<ApiResult<SessionPlan>> ReviewAsync(IslandBootstrapDto bootstrap, ReviewSessionRequest request, CancellationToken ct);

    Task<ApiResult<ProfileDto>> ProfileAsync(IslandBootstrapDto bootstrap, CancellationToken ct);

    /// <summary>Called when a Practice or Review session reached its summary.</summary>
    Task OnSessionFinishedAsync(IslandBootstrapDto bootstrap, CancellationToken ct);

    /// <summary>
    /// Called with the summary of a mock whose answers are in the outbox. May be called again for the same mock (a reload
    /// resumes an unfinished hand-over), so it must be idempotent by <paramref name="mockSessionId"/>.
    /// </summary>
    Task OnMockFinishedAsync(IslandBootstrapDto bootstrap, string mockSessionId, MockSummaryDto summary, CancellationToken ct);
}
