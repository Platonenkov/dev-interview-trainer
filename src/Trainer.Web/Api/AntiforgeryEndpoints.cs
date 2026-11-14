using Microsoft.AspNetCore.Antiforgery;
using Trainer.Contracts;
using Trainer.Web.Auth;
using Trainer.Web.Security;

namespace Trainer.Web.Api;

/// <summary>
/// <c>GET /api/antiforgery</c> (spec 8.8): the token every write sends back. The token is bound to the current user,
/// so a client fetches a new one after signing in or out and after a 400 <c>antiforgery</c>. The response is never
/// compressed (<see cref="BreachGuardMiddleware"/>) and never stored.
/// </summary>
public static class AntiforgeryEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        // Derived from the one constant the compression guard matches, so renaming the route cannot silently
        // re-enable compression of the token.
        api.MapGet(BreachGuardMiddleware.AntiforgeryPath[ApiEndpoints.Prefix.Length..], Get);
    }

    private static IResult Get(HttpContext context, IAntiforgery antiforgery)
    {
        // A session that could not be revalidated is still signed in: a token issued now would be bound to the
        // anonymous principal and fail the user's next write. 503 makes the client retry later with back-off instead.
        if (RevalidationOutage.IsMarked(context))
        {
            return ApiEndpoints.Error(StatusCodes.Status503ServiceUnavailable, ApiErrorCodes.ServerError);
        }

        AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(context);
        string requestToken = tokens.RequestToken ?? throw new InvalidOperationException("Antiforgery issued no request token.");
        string headerName = tokens.HeaderName ?? throw new InvalidOperationException("Antiforgery has no header name configured.");
        return Results.Json(new AntiforgeryTokenDto(requestToken, headerName), TrainerJsonContext.Default.AntiforgeryTokenDto);
    }
}
