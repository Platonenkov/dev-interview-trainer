using Microsoft.AspNetCore.Mvc;
using Trainer.Application.Sessions;
using Trainer.Contracts;
using Trainer.Domain.Sessions;
using Trainer.Domain.Users;
using Trainer.Web.Auth;
using Trainer.Web.ContentState;

namespace Trainer.Web.Api;

/// <summary>
/// Session reads. They are <c>POST</c> because an anonymous <c>new</c> filter carries up to 1000 excluded ids, yet they
/// change nothing, so they take no antiforgery token (ADR 0008): only <c>application/json</c> binds (415 otherwise) and
/// there is no CORS policy, so a cross-site page can neither send one without a preflight nor read the answer.
/// </summary>
public static class SessionEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder sessions = api.MapGroup("/sessions");
        sessions.AddEndpointFilter<ReadOwnerCheckFilter>();
        sessions.MapPost("/practice", PracticeAsync).RequireRateLimiting(ApiRateLimits.Reads);
        sessions.MapPost("/mock", MockAsync).RequireRateLimiting(ApiRateLimits.Reads);
        sessions.MapPost("/review", ReviewAsync).RequireRateLimiting(ApiRateLimits.Reads).RequireAuthorization();
    }

    private static async Task<IResult> PracticeAsync(
        [FromBody] PracticeSessionRequest? request,
        HttpContext context,
        ContentSnapshotProvider content,
        ISessionQueries sessions,
        CancellationToken ct)
    {
        if (ApiEndpoints.TopicsOf(content.Current, request?.Stack) is not { } topics
            || !ApiRequestParser.TryParsePractice(request, topics, out PracticeCriteria? criteria, out _))
        {
            return ApiEndpoints.Validation();
        }

        UserId? user = context.User.GetUserId();
        if (criteria.Status == PracticeStatus.Due && user is null)
        {
            return ApiEndpoints.SignInRequired(context);
        }

        SessionDto session = await sessions.GetPracticeAsync(criteria, user, ct);
        return Results.Json(session, TrainerJsonContext.Default.SessionDto);
    }

    private static async Task<IResult> MockAsync(
        [FromBody] MockSessionRequest? request,
        ContentSnapshotProvider content,
        ISessionQueries sessions,
        CancellationToken ct)
    {
        if (ApiEndpoints.TopicsOf(content.Current, request?.Stack) is not { } topics
            || !ApiRequestParser.TryParseMock(request, topics, out MockCriteria? criteria, out _))
        {
            return ApiEndpoints.Validation();
        }

        MockSessionDto session = await sessions.GetMockAsync(criteria, ct);
        return Results.Json(session, TrainerJsonContext.Default.MockSessionDto);
    }

    private static async Task<IResult> ReviewAsync(
        [FromBody] ReviewSessionRequest? request,
        HttpContext context,
        ContentSnapshotProvider content,
        ISessionQueries sessions,
        CancellationToken ct)
    {
        if (context.User.GetUserId() is not { } user)
        {
            return ApiEndpoints.SignInRequired(context);
        }

        if (!content.Current.HasStack(request?.Stack) || !ApiRequestParser.TryParseReview(request, out ReviewCriteria? criteria, out _))
        {
            return ApiEndpoints.Validation();
        }

        SessionDto session = await sessions.GetReviewAsync(criteria, user, ct);
        return Results.Json(session, TrainerJsonContext.Default.SessionDto);
    }
}
