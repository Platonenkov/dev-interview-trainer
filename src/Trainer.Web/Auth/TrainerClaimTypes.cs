namespace Trainer.Web.Auth;

/// <summary>
/// Claim types of the session principal. The Telegram ones are the raw id_token types the library keeps
/// (<c>MapInboundClaims</c> is off); the <c>dit:</c> ones come from <see cref="UserClaimsFactory"/>. Roles use
/// <see cref="System.Security.Claims.ClaimTypes.Role"/>.
/// </summary>
public static class TrainerClaimTypes
{
    /// <summary>The Bot-API user id, added by the library after the gate allows the sign-in.</summary>
    public const string TelegramUserId = "telegram_user_id";

    public const string UserId = "dit:uid";
    public const string SecurityStamp = "dit:stamp";
    public const string DisplayName = "name";
    public const string Username = "preferred_username";

    /// <summary>Telegram's OIDC subject; never the Bot-API id. Antiforgery binds tokens to it.</summary>
    public const string Subject = "sub";
}
