using Trainer.Domain.Keys;
using Trainer.Domain.Users;

namespace Trainer.Application.Users;

/// <summary>
/// What authentication needs to know about a user. <paramref name="Roles"/> are the stored roles plus Admin for a
/// configured admin id, distinct and ordinal-sorted.
/// </summary>
public sealed record UserAccess(
    UserId UserId,
    long TelegramUserId,
    string SecurityStamp,
    bool IsBlocked,
    IReadOnlyList<string> Roles,
    string? DisplayName,
    string? Username,
    CultureKey? PreferredCulture);
