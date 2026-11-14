using Trainer.Contracts;
using Trainer.Web.Auth;

namespace Trainer.Web.Api;

/// <summary>
/// ADR 0008 for reads: an island names the account its page was rendered for in <see cref="OwnerCheckFilter.OwnerHeader"/>
/// (a Telegram user id, or <see cref="Anonymous"/>). When that is not who the cookie says is signed in now (another tab
/// signed in or out), the read is answered 409 <c>owner-mismatch</c>, so a stale tab never shows the new account's
/// sessions or progress under its old state. A page bound to a user while no session is signed in at all (the cookie
/// expired, or ended in another tab) gets 401 <c>sign-in-required</c> instead, which the island shows as "signed out"
/// (503 <c>server-error</c> when the session is only unverified because of a database failure).
/// A read without the header is served as before: the header only lets a client ask for the check.
/// </summary>
public sealed class ReadOwnerCheckFilter : IEndpointFilter
{
    public const string Anonymous = "anon";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        HttpContext http = context.HttpContext;
        if (http.Request.Headers.TryGetValue(OwnerCheckFilter.OwnerHeader, out Microsoft.Extensions.Primitives.StringValues values))
        {
            long? signedIn = http.User.GetTelegramUserId();
            if (!Matches(values, signedIn))
            {
                return signedIn is null && !IsAnonymous(values) && OwnerCheckFilter.ParseOwner(values) is not null
                    ? ApiEndpoints.SignInRequired(http)
                    : ApiEndpoints.Error(StatusCodes.Status409Conflict, ApiErrorCodes.OwnerMismatch);
            }
        }

        return await next(context);
    }

    private static bool IsAnonymous(Microsoft.Extensions.Primitives.StringValues values) =>
        values.Count == 1 && string.Equals(values[0], Anonymous, StringComparison.Ordinal);

    private static bool Matches(Microsoft.Extensions.Primitives.StringValues values, long? signedIn) =>
        IsAnonymous(values)
            ? signedIn is null
            : OwnerCheckFilter.ParseOwner(values) is { } owner && owner == signedIn;
}
