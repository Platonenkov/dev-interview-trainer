using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Trainer.Web.Auth;

/// <summary>
/// What an island learns about the signed-in user (spec 8.8): the Telegram user id, a display name and the roles.
/// The session's internal claims (user id, security stamp, OIDC subject, picture) never reach the browser's runtime.
/// </summary>
public static class AuthenticationStateSerialization
{
    /// <summary>The claim type the island reads the display name from.</summary>
    public const string NameClaimType = TrainerClaimTypes.DisplayName;

    /// <summary>
    /// The custom claim set for <c>AuthenticationStateSerializationOptions.SerializationCallback</c>; null (nothing
    /// sent, the island sees an anonymous user) when the user is not signed in.
    /// </summary>
    public static ValueTask<AuthenticationStateData?> SerializeAsync(AuthenticationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ClaimsPrincipal user = state.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            return ValueTask.FromResult<AuthenticationStateData?>(null);
        }

        AuthenticationStateData data = new() { NameClaimType = NameClaimType, RoleClaimType = ClaimTypes.Role };
        if (user.FindFirst(TrainerClaimTypes.TelegramUserId)?.Value is { } telegramId)
        {
            data.Claims.Add(new ClaimData(TrainerClaimTypes.TelegramUserId, telegramId));
        }

        if (user.GetDisplayName() is { } name)
        {
            data.Claims.Add(new ClaimData(NameClaimType, name));
        }

        foreach (Claim role in user.FindAll(ClaimTypes.Role))
        {
            data.Claims.Add(new ClaimData(ClaimTypes.Role, role.Value));
        }

        return ValueTask.FromResult<AuthenticationStateData?>(data);
    }
}
