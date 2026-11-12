using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Trainer.Web.Startup;

public sealed class ReadinessHealthCheck(ReadinessState state) : IHealthCheck
{
    public const string Tag = "ready";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(state.Status switch
        {
            ReadinessStatus.Ready => HealthCheckResult.Healthy(),
            ReadinessStatus.Failed => HealthCheckResult.Unhealthy("Content import failed."),
            _ => HealthCheckResult.Unhealthy("Content import is not finished."),
        });
}
