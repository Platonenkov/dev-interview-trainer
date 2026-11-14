using Trainer.Domain.Users;

namespace Trainer.Application.Admin;

/// <summary>One user in the admin list. <paramref name="Roles"/> include Admin for a configured admin id.</summary>
public sealed record AdminUserRow(
    UserId Id,
    long TelegramUserId,
    string? DisplayName,
    string? Username,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSignInAt,
    bool IsBlocked,
    IReadOnlyList<string> Roles);

/// <summary>One page of <see cref="AdminUserRow"/>; <paramref name="Page"/> is the page actually returned (1-based, clamped).</summary>
public sealed record UserPage(IReadOnlyList<AdminUserRow> Rows, int Total, int Page, int PageCount);
