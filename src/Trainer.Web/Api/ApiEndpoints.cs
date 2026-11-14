using Trainer.Contracts;
using Trainer.Domain.Keys;
using Trainer.Web.Auth;
using Trainer.Web.ContentState;

namespace Trainer.Web.Api;

/// <summary>
/// Maps the minimal API the islands talk to (spec 8.8). Bodies and responses go through <see cref="TrainerJsonContext"/>;
/// errors are <see cref="ApiErrorDto"/> bodies, written by the endpoints or, for bodiless statuses, by
/// <see cref="ApiErrorMiddleware"/>.
/// </summary>
public static class ApiEndpoints
{
    public const string Prefix = "/api";

    public static void MapTrainerApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder api = app.MapGroup(Prefix);
        AntiforgeryEndpoints.Map(api);
        SessionEndpoints.Map(api);
        AnswerEndpoints.Map(api);
        MockSessionEndpoints.Map(api);
        ProfileEndpoints.Map(api);
    }

    /// <summary>Signed-in writes: rate limited per user, then the antiforgery token, then the outbox owner (ADR 0008).</summary>
    internal static RouteHandlerBuilder AsWrite(this RouteHandlerBuilder endpoint) => endpoint
        .RequireAuthorization()
        .RequireRateLimiting(ApiRateLimits.Writes)
        .AddEndpointFilter<AntiforgeryValidationFilter>()
        .AddEndpointFilter<OwnerCheckFilter>();

    internal static IResult Error(int status, string code) =>
        Results.Json(new ApiErrorDto(code, null), TrainerJsonContext.Default.ApiErrorDto, statusCode: status);

    /// <summary>
    /// 401 <c>sign-in-required</c> for a request with no session, or 503 <c>server-error</c> when the request is
    /// anonymous only because its session could not be revalidated (<see cref="RevalidationOutage"/>): the client
    /// stops syncing on a 401 for good but retries a 5xx.
    /// </summary>
    internal static IResult SignInRequired(HttpContext context) => RevalidationOutage.IsMarked(context)
        ? Error(StatusCodes.Status503ServiceUnavailable, ApiErrorCodes.ServerError)
        : Error(StatusCodes.Status401Unauthorized, ApiErrorCodes.SignInRequired);

    internal static IResult Validation() => Error(StatusCodes.Status400BadRequest, ApiErrorCodes.Validation);

    /// <summary>The stack's topics, or null when the stack does not exist.</summary>
    internal static IReadOnlySet<TopicKey>? TopicsOf(ContentSnapshot content, string? stack)
    {
        if (!content.HasStack(stack))
        {
            return null;
        }

        HashSet<TopicKey> topics = [];
        foreach (string topic in content.TopicsOf(stack!))
        {
            topics.Add(new TopicKey(topic));
        }

        return topics;
    }
}
