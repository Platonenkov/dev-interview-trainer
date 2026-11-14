using Trainer.Domain.Keys;
using Trainer.Domain.Users;

namespace Trainer.Application.Users;

public interface IUserAccountService
{
    /// <summary>
    /// Creates the user on the first sign-in and refreshes the profile and <c>last_sign_in_at</c> afterwards. Safe
    /// under parallel first sign-ins of the same Telegram id: both get the same <see cref="UserId"/>.
    /// </summary>
    Task<UserAccess> SignInAsync(TelegramProfile profile, CancellationToken ct);

    /// <summary>Returns the access record, or null when the Telegram id has never signed in.</summary>
    Task<UserAccess?> GetAccessAsync(long telegramUserId, CancellationToken ct);

    /// <summary>
    /// Blocks or unblocks the user and rotates the security stamp, which invalidates live sessions. Blocking a
    /// configured admin returns <see cref="BlockResult.ProtectedAdmin"/>; unblocking one is allowed.
    /// </summary>
    Task<BlockResult> SetBlockedAsync(UserId userId, bool blocked, CancellationToken ct);

    /// <summary>Replaces the stored roles and rotates the security stamp.</summary>
    /// <exception cref="ArgumentException">A role name is unknown or is <see cref="Roles.Admin"/>, which is computed from configuration and never stored.</exception>
    /// <exception cref="InvalidOperationException">The user does not exist.</exception>
    Task SetRolesAsync(UserId userId, IReadOnlyCollection<string> roles, CancellationToken ct);

    /// <exception cref="InvalidOperationException">The user does not exist.</exception>
    Task SetPreferredCultureAsync(UserId userId, CultureKey culture, CancellationToken ct);
}
