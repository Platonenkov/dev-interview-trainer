using Trainer.Contracts;
using Trainer.Web.Client.Api;
using Trainer.Web.Client.Storage;

namespace Trainer.Web.Client.Backend;

/// <summary>The server edition's backend: one <c>/api</c> request per session, its questions preloaded into the plan.</summary>
public sealed class ServerIslandBackend(TrainerApiClient api) : IIslandBackend
{
    public bool KeepsProgress(IslandBootstrapDto bootstrap)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        return bootstrap.Me is not null;
    }

    public string OutboxKey(IslandBootstrapDto bootstrap)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        return bootstrap.Me is { } me ? StorageKeys.UserOutbox(me.TelegramUserId) : StorageKeys.AnonymousOutbox;
    }

    public bool FiltersPendingAnswers => true;

    public bool SendsAnsweredAsExclude => true;

    public Task<IslandProgress?> GetProgressAsync(IslandBootstrapDto bootstrap, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        return Task.FromResult(IslandProgress.From(bootstrap.Me));
    }

    public async Task<ApiResult<SessionPlan>> PracticeAsync(IslandBootstrapDto bootstrap, PracticeSessionRequest request, CancellationToken ct) =>
        ToPlan(await api.PracticeAsync(request, ct));

    public Task<ApiResult<MockSessionDto>> MockAsync(IslandBootstrapDto bootstrap, MockSessionRequest request, CancellationToken ct) =>
        api.MockAsync(request, ct);

    public async Task<ApiResult<SessionPlan>> ReviewAsync(IslandBootstrapDto bootstrap, ReviewSessionRequest request, CancellationToken ct) =>
        ToPlan(await api.ReviewAsync(request, ct));

    public Task<ApiResult<ProfileDto>> ProfileAsync(IslandBootstrapDto bootstrap, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        return api.ProfileAsync(bootstrap.Culture, bootstrap.Stack, ct);
    }

    public Task OnSessionFinishedAsync(IslandBootstrapDto bootstrap, CancellationToken ct) => Task.CompletedTask;

    // The summary reaches the server through the pending queue (OutboxSyncService); nothing is kept beside it.
    public Task OnMockFinishedAsync(IslandBootstrapDto bootstrap, string mockSessionId, MockSummaryDto summary, CancellationToken ct) =>
        Task.CompletedTask;

    private static ApiResult<SessionPlan> ToPlan(ApiResult<SessionDto> result)
    {
        SessionPlan? plan = result.Value is { } session
            ? new SessionPlan([.. session.Questions.Select(q => q.Id)], session.PoolSize, session.MyRatings, new PreloadedQuestionSource(session.Questions))
            : null;
        return new ApiResult<SessionPlan>(plan, result.Status, result.ErrorCode, result.RetryAfter);
    }
}
