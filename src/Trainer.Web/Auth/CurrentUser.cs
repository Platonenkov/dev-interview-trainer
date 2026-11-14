using System.Globalization;
using System.Security.Claims;
using Trainer.Domain.Users;

namespace Trainer.Web.Auth;

/// <summary>Reads the application identity out of a session principal.</summary>
public static class CurrentUser
{
    /// <summary>The application user id, or null for an anonymous or malformed principal.</summary>
    public static UserId? GetUserId(this ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.Identity?.IsAuthenticated == true && UserId.TryCreate(user.FindFirst(TrainerClaimTypes.UserId)?.Value, out UserId id)
            ? id
            : null;
    }

    /// <summary>The Telegram Bot-API user id, or null for an anonymous or malformed principal.</summary>
    public static long? GetTelegramUserId(this ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.Identity?.IsAuthenticated == true
            && long.TryParse(user.FindFirst(TrainerClaimTypes.TelegramUserId)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out long id)
            ? id
            : null;
    }

    /// <summary>The Telegram display name, else the username, else null; for display only.</summary>
    public static string? GetDisplayName(this ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        string? name = user.FindFirst(TrainerClaimTypes.DisplayName)?.Value;
        return string.IsNullOrWhiteSpace(name) ? NullIfBlank(user.FindFirst(TrainerClaimTypes.Username)?.Value) : name;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
