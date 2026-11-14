using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Trainer.Domain.Users;
using Trainer.Web.Auth;
using Trainer.Web.Catalog;

namespace Trainer.Web.Api;

/// <summary>The <c>/api</c> rate limit policies: fixed one-minute windows sized by <see cref="RateLimitOptions"/>.</summary>
public static class ApiRateLimits
{
    public const string Writes = "api-writes";
    public const string Reads = "api-reads";

    /// <summary>The partition of requests that have neither a user nor a client address.</summary>
    public const string FallbackPartition = "unknown";

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>Reads <see cref="RateLimitOptions.SectionName"/> at startup.</summary>
    /// <exception cref="InvalidOperationException">A limit is not a positive integer.</exception>
    public static RateLimitOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        RateLimitOptions options = configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>() ?? new RateLimitOptions();
        EnsurePositive(nameof(RateLimitOptions.WritesPerMinute), options.WritesPerMinute);
        EnsurePositive(nameof(RateLimitOptions.ReadsPerMinute), options.ReadsPerMinute);
        EnsurePositive(nameof(RateLimitOptions.SearchPerMinute), options.SearchPerMinute);
        return options;
    }

    /// <summary>
    /// Registers both policies, the catalog search limiter and <see cref="IOptions{RateLimitOptions}"/> holding exactly the limits they enforce. A
    /// rejected request gets 429 with <c>Retry-After</c>; the JSON body of an <c>/api</c> request is written by
    /// <see cref="ApiErrorMiddleware"/>, a rejected catalog search gets a localised HTML page instead.
    /// </summary>
    public static IServiceCollection AddTrainerApiRateLimits(this IServiceCollection services, RateLimitOptions limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        services.AddSingleton(Options.Create(limits));
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, ct) =>
            {
                TimeSpan retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan metadata) ? metadata : Window;
                context.HttpContext.Response.Headers[HeaderNames.RetryAfter] =
                    Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                if (CatalogSearchRateLimit.IsSearchRequest(context.HttpContext))
                {
                    await CatalogSearchRateLimit.WriteRejectionAsync(context.HttpContext, ct);
                }
            };
            // Only search requests are partitioned; every other request, /api included, passes the global limiter untouched.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                CatalogSearchRateLimit.IsSearchRequest(context)
                    ? FixedWindow(CatalogSearchRateLimit.Partition(context), limits.SearchPerMinute)
                    : RateLimitPartition.GetNoLimiter("unlimited"));
            options.AddPolicy(Writes, context => FixedWindow(WritePartition(context), limits.WritesPerMinute));
            options.AddPolicy(Reads, context => FixedWindow(ReadPartition(context), limits.ReadsPerMinute));
        });
        return services;
    }

    /// <summary>Writes require a signed-in user, so they are counted per user; the address only backs up a malformed principal.</summary>
    public static string WritePartition(HttpContext context) => ReadPartition(context);

    /// <summary>The signed-in user, else the client address, else <see cref="FallbackPartition"/>.</summary>
    public static string ReadPartition(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.User.GetUserId() is UserId user)
        {
            return "user:" + user.Value;
        }

        return context.Connection.RemoteIpAddress is { } address ? "ip:" + address : FallbackPartition;
    }

    private static RateLimitPartition<string> FixedWindow(string key, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = Window,
            QueueLimit = 0,
            AutoReplenishment = true,
        });

    private static void EnsurePositive(string name, int value)
    {
        if (value <= 0)
        {
            throw new InvalidOperationException(
                $"{RateLimitOptions.SectionName}:{name} is {value.ToString(CultureInfo.InvariantCulture)}; it must be a positive number of requests per minute.");
        }
    }
}
