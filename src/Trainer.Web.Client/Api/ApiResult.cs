namespace Trainer.Web.Client.Api;

/// <summary>The outcome of one <c>/api</c> call.</summary>
/// <param name="Value">The response body of a success; for a write without a body, true on success.</param>
/// <param name="Status">The HTTP status, or 0 when the request never got a response (offline, DNS, aborted).</param>
/// <param name="ErrorCode">The <c>ApiErrorDto.Code</c> of an error response; null for a success or a non-JSON body.</param>
/// <param name="RetryAfter">The response's <c>Retry-After</c> as a delay (the rate limiter sends it with 429); null without one.</param>
public sealed record ApiResult<T>(T? Value, int Status, string? ErrorCode, TimeSpan? RetryAfter = null)
{
    public bool IsSuccess => Status is >= 200 and < 300;
}
