using Microsoft.Extensions.Options;
using Trainer.Web.Auth;
using Trainer.Web.Internal;

namespace Trainer.Web.Startup;

/// <summary>
/// While the file at <c>App:MaintenanceFlagPath</c> exists, answers 503 with <c>Retry-After</c> and an empty body to every
/// request that could write: any method other than GET, HEAD and OPTIONS, and everything under the sign-in callback
/// path, which is a GET that creates or updates users. It runs before routing so no handler, form post or <c>/api</c>
/// write is reached. This is the second of two layers: nginx already answers 503 to every request, GET included, while
/// the flag exists, so a read only gets through here when it does not come through nginx. The gate still lives here
/// because nginx buffers a request body before proxying, so a request admitted before the flag appeared can arrive
/// after it. Only the <c>/internal</c> path prefix on the internal port is exempt (the deployment imports content
/// through it during a restore); the pages and <c>/api</c> are also served on that port and stay gated. The flag is
/// checked at most once a second. An unreadable flag (a missing mount, a permission error) counts as absent, so a
/// broken mount never takes the site down.
/// </summary>
public sealed class MaintenanceGateMiddleware(
    RequestDelegate next,
    IOptions<AppOptions> appOptions,
    IOptions<InternalOptions> internalOptions,
    TimeProvider time)
{
    private const string RetryAfterSeconds = "60";
    private const string InternalPrefix = "/internal";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(1);

    private readonly string? _flagPath = string.IsNullOrWhiteSpace(appOptions.Value.MaintenanceFlagPath)
        ? null
        : appOptions.Value.MaintenanceFlagPath;

    private FlagReading _reading = new(false, long.MinValue);

    private sealed record FlagReading(bool Present, long Timestamp);

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_flagPath is null || !IsWrite(context.Request) || IsInternalRequest(context) || !FlagPresent())
        {
            return next(context);
        }

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = RetryAfterSeconds;
        context.Response.Headers.CacheControl = "no-store";
        return Task.CompletedTask;
    }

    private static bool IsWrite(HttpRequest request) =>
        !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method))
        || request.Path.StartsWithSegments(TelegramAuth.CallbackPath, StringComparison.OrdinalIgnoreCase);

    private bool IsInternalRequest(HttpContext context)
    {
        int port = internalOptions.Value.Port;
        return port > 0
            && context.Connection.LocalPort == port
            && context.Request.Path.StartsWithSegments(InternalPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private bool FlagPresent()
    {
        FlagReading current = Volatile.Read(ref _reading);
        long now = time.GetTimestamp();
        if (current.Timestamp != long.MinValue && time.GetElapsedTime(current.Timestamp, now) < CacheLifetime)
        {
            return current.Present;
        }

        bool present = File.Exists(_flagPath);
        Volatile.Write(ref _reading, new FlagReading(present, now));
        return present;
    }
}
