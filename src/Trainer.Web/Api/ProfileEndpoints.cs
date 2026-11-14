using Trainer.Application.Progress;
using Trainer.Contracts;
using Trainer.Domain.Keys;
using Trainer.Web.Auth;
using Trainer.Web.ContentState;

namespace Trainer.Web.Api;

/// <summary><c>GET /api/profile?culture=&amp;stack=</c>: the signed-in user's progress in one stack.</summary>
public static class ProfileEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGet("/profile", GetAsync)
            .RequireAuthorization()
            .RequireRateLimiting(ApiRateLimits.Reads)
            .AddEndpointFilter<ReadOwnerCheckFilter>();
    }

    private static async Task<IResult> GetAsync(
        string? culture,
        string? stack,
        HttpContext context,
        ContentSnapshotProvider content,
        IProgressQueries progress,
        CancellationToken ct)
    {
        if (context.User.GetUserId() is not { } user)
        {
            return ApiEndpoints.SignInRequired(context);
        }

        if (!CultureKey.TryCreate(culture, out CultureKey cultureKey)
            || !content.Current.HasStack(stack)
            || !StackKey.TryCreate(stack, out StackKey stackKey))
        {
            return ApiEndpoints.Validation();
        }

        ProfileDto profile = await progress.GetProfileAsync(user, cultureKey, stackKey, ct);
        return Results.Json(profile, TrainerJsonContext.Default.ProfileDto);
    }
}
