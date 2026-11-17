using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Trainer.Web.Api;

/// <summary>
/// The per-user write limit of <see cref="ApiRateLimits.Writes"/> for the static feedback forms, which are page posts and
/// so never pass the <c>/api</c> rate limiter. Same partition and the same <see cref="RateLimitOptions.WritesPerMinute"/>;
/// a separate window, because the endpoint middleware cannot be attached to the POST of a page without also counting its GETs.
/// </summary>
public sealed class FormWriteRateLimiter(IOptions<RateLimitOptions> limits) : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(key =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limits.Value.WritesPerMinute,
            Window = ApiRateLimits.Window,
            QueueLimit = 0,
            AutoReplenishment = true,
        }));

    /// <summary>Takes one permit for the request's user; false when the user is over the limit for this minute.</summary>
    public bool TryAcquire(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        using RateLimitLease lease = _limiter.AttemptAcquire(ApiRateLimits.WritePartition(context));
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
