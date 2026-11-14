namespace Trainer.Application.Users;

/// <summary>Access configuration bound once at startup from <c>App:AdminTelegramIds</c>; a change needs a restart.</summary>
public sealed class AccessOptions
{
    /// <summary>
    /// Telegram user ids that always hold the Admin role. The role is computed from this list and never stored, so
    /// removing an id and restarting revokes it at the next revalidation without a data change.
    /// </summary>
    public IReadOnlyList<long> AdminTelegramIds { get; set; } = [];
}
