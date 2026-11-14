using System.Security.Claims;
using Microsoft.Extensions.Localization;
using Trainer.Web.Auth;

namespace Trainer.Web.Components.Layout;

/// <summary>The name shown for a signed-in user, shared by the header and the sign-in page.</summary>
public static class AccountName
{
    /// <summary>The display name, else the username, else the localised "Telegram user".</summary>
    public static string For(ClaimsPrincipal user, IStringLocalizer<SharedResource> localizer)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(localizer);
        return user.GetDisplayName() ?? localizer["Account.TelegramUser"];
    }
}
