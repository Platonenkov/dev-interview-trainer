using Telegram.OpenIdConnect.AspNetCore;
using Trainer.Application.Users;

namespace Trainer.Web.Auth;

/// <summary>
/// Decides every Telegram sign-in against the database: records the user (created on first sign-in), refuses a
/// blocked one and attaches the application claims.
/// </summary>
public sealed class TrainerSignInGate(IUserAccountService users, UserClaimsFactory claims) : ITelegramSignInGate
{
    public ValueTask<TelegramSignInDecision> DecideAsync(long telegramUserId, CancellationToken ct) =>
        DecideAsync(new TelegramSignInIdentity(telegramUserId), ct);

    /// <summary>The overload the library calls; it carries the names Telegram sent with the id.</summary>
    public async ValueTask<TelegramSignInDecision> DecideAsync(TelegramSignInIdentity identity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identity);

        UserAccess access = await users.SignInAsync(
            new TelegramProfile(identity.TelegramUserId, identity.Username, identity.DisplayName), ct);
        return access.IsBlocked
            ? new TelegramSignInDecision(false)
            : new TelegramSignInDecision(true, claims.Create(access));
    }
}
