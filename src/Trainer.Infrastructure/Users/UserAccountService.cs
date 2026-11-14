using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Trainer.Application.Users;
using Trainer.Domain.Ids;
using Trainer.Domain.Keys;
using Trainer.Domain.Users;
using Trainer.Infrastructure.Persistence;
using Trainer.Infrastructure.Persistence.Entities;

namespace Trainer.Infrastructure.Users;

internal sealed class UserAccountService(
    TrainerDbContext db,
    IOptions<AccessOptions> access,
    UlidGenerator ids,
    TimeProvider time) : IUserAccountService
{
    private const int StampBytes = 16;

    private readonly HashSet<long> _adminIds = [.. access.Value.AdminTelegramIds];

    public async Task<UserAccess> SignInAsync(TelegramProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        DateTimeOffset now = time.GetUtcNow();
        string id = ids.Next();
        string stamp = NewStamp();

        // One statement, so two parallel first sign-ins of the same Telegram id cannot race into a unique violation:
        // the loser waits for the winner's commit and takes the update branch. The key columns are never rewritten.
        // A claim Telegram omits keeps the stored value instead of erasing it.
        // The result is materialised with ToListAsync because an INSERT cannot be composed into a LIMIT query.
        List<UserEntity> rows = await db.Users
            .FromSql($"""
                INSERT INTO users (id, telegram_user_id, display_name, username, created_at, last_sign_in_at, security_stamp)
                VALUES ({id}, {profile.TelegramUserId}, {profile.DisplayName}, {profile.Username}, {now}, {now}, {stamp})
                ON CONFLICT (telegram_user_id) DO UPDATE
                SET display_name = COALESCE(EXCLUDED.display_name, users.display_name),
                    username = COALESCE(EXCLUDED.username, users.username),
                    last_sign_in_at = EXCLUDED.last_sign_in_at
                RETURNING *
                """)
            .AsNoTracking()
            .ToListAsync(ct);
        UserEntity user = rows.Single();

        List<string> storedRoles = await db.UserRoles
            .AsNoTracking()
            .Where(r => r.UserId == user.Id)
            .Select(r => r.Role)
            .ToListAsync(ct);
        return ToAccess(user, storedRoles);
    }

    public async Task<UserAccess?> GetAccessAsync(long telegramUserId, CancellationToken ct)
    {
        var row = await db.Users
            .AsNoTracking()
            .Where(u => u.TelegramUserId == telegramUserId)
            .Select(u => new
            {
                User = u,
                Roles = db.UserRoles.Where(r => r.UserId == u.Id).Select(r => r.Role).ToList(),
            })
            .SingleOrDefaultAsync(ct);
        return row is null ? null : ToAccess(row.User, row.Roles);
    }

    public async Task<BlockResult> SetBlockedAsync(UserId userId, bool blocked, CancellationToken ct)
    {
        long? telegramUserId = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => (long?)u.TelegramUserId)
            .SingleOrDefaultAsync(ct);
        if (telegramUserId is null)
        {
            return BlockResult.NotFound;
        }

        // Only blocking is refused: an admin blocked before being configured must stay recoverable.
        if (blocked && _adminIds.Contains(telegramUserId.Value))
        {
            return BlockResult.ProtectedAdmin;
        }

        string stamp = NewStamp();
        int affected = await db.Users
            .Where(u => u.Id == userId.Value)
            .ExecuteUpdateAsync(
                s => s.SetProperty(u => u.IsBlocked, blocked).SetProperty(u => u.SecurityStamp, stamp),
                ct);
        return affected == 0 ? BlockResult.NotFound : BlockResult.Updated;
    }

    public async Task SetRolesAsync(UserId userId, IReadOnlyCollection<string> roles, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roles);

        // Roles are validated before any write so an unknown name leaves the stored state untouched.
        string[] distinctRoles = [.. roles.Distinct(StringComparer.Ordinal)];
        foreach (string role in distinctRoles)
        {
            // Admin is computed from AccessOptions only; a stored row would outlive its removal from the config.
            if (string.Equals(role, Roles.Admin, StringComparison.Ordinal) || !Roles.All.Contains(role, StringComparer.Ordinal))
            {
                throw new ArgumentException($"Unknown role '{role}'.", nameof(roles));
            }
        }

        string stamp = NewStamp();
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(ct);
        int affected = await db.Users
            .Where(u => u.Id == userId.Value)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.SecurityStamp, stamp), ct);
        if (affected == 0)
        {
            throw new InvalidOperationException("The user does not exist.");
        }

        await db.UserRoles.Where(r => r.UserId == userId.Value).ExecuteDeleteAsync(ct);
        db.UserRoles.AddRange(distinctRoles.Select(role => new UserRoleEntity { UserId = userId.Value, Role = role }));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        db.ChangeTracker.Clear();
    }

    public async Task SetPreferredCultureAsync(UserId userId, CultureKey culture, CancellationToken ct)
    {
        string value = culture.Value;
        int affected = await db.Users
            .Where(u => u.Id == userId.Value)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.PreferredCulture, value), ct);
        if (affected == 0)
        {
            throw new InvalidOperationException("The user does not exist.");
        }
    }

    private UserAccess ToAccess(UserEntity user, IReadOnlyCollection<string> storedRoles)
    {
        IEnumerable<string> all = _adminIds.Contains(user.TelegramUserId) ? storedRoles.Append(Roles.Admin) : storedRoles;
        string[] roles = [.. all.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        CultureKey? culture = CultureKey.TryCreate(user.PreferredCulture, out CultureKey parsed) ? parsed : null;
        return new UserAccess(
            new UserId(user.Id),
            user.TelegramUserId,
            user.SecurityStamp,
            user.IsBlocked,
            roles,
            user.DisplayName,
            user.Username,
            culture);
    }

    private static string NewStamp() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(StampBytes));
}
