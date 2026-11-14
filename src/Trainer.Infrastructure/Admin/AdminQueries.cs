using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Trainer.Application.Admin;
using Trainer.Application.Users;
using Trainer.Domain.Users;
using Trainer.Infrastructure.Persistence;
using Trainer.Infrastructure.Persistence.Entities;

namespace Trainer.Infrastructure.Admin;

internal sealed class AdminQueries(TrainerDbContext db, IOptions<AccessOptions> access, TimeProvider clock) : IAdminQueries
{
    private const string LikeEscape = "\\";

    private readonly HashSet<long> _adminIds = [.. access.Value.AdminTelegramIds];

    public async Task<AdminOverview> GetOverviewAsync(CancellationToken ct)
    {
        try
        {
            if (!await db.Database.CanConnectAsync(ct))
            {
                return Unreachable();
            }

            return await ReadOverviewAsync(ct);
        }
        catch (DbException)
        {
            // The connection can drop between the probe and the reads; the page reports it instead of failing.
            return Unreachable();
        }
    }

    public async Task<UserPage> GetUsersAsync(string? search, int page, CancellationToken ct)
    {
        IQueryable<UserEntity> query = Filter(db.Users.AsNoTracking(), NormalizeSearch(search));
        int total = await query.CountAsync(ct);
        int pageCount = Math.Max(1, (total + AdminLimits.UsersPerPage - 1) / AdminLimits.UsersPerPage);
        int current = Math.Clamp(page, 1, pageCount);

        List<UserEntity> users = await query
            .OrderByDescending(u => u.LastSignInAt)
            .ThenByDescending(u => u.Id)
            .Skip((current - 1) * AdminLimits.UsersPerPage)
            .Take(AdminLimits.UsersPerPage)
            .ToListAsync(ct);

        string[] ids = [.. users.Select(u => u.Id)];
        ILookup<string, string> storedRoles = (await db.UserRoles
                .AsNoTracking()
                .Where(r => ids.Contains(r.UserId))
                .Select(r => new { r.UserId, r.Role })
                .ToListAsync(ct))
            .ToLookup(r => r.UserId, r => r.Role, StringComparer.Ordinal);

        List<AdminUserRow> rows = [.. users.Select(u => ToRow(u, storedRoles[u.Id]))];
        return new UserPage(rows, total, current, pageCount);
    }

    private async Task<AdminOverview> ReadOverviewAsync(CancellationToken ct)
    {
        DateTimeOffset since = clock.GetUtcNow().AddHours(-24);
        List<ImportRow> imports = await db.ContentImports
            .AsNoTracking()
            .OrderByDescending(i => i.ImportedAt)
            .ThenByDescending(i => i.Id)
            .Take(AdminLimits.RecentImportCount)
            .Select(i => new ImportRow(i.Hash, i.SchemaVersion, i.RenderVersion, i.ImportedAt, i.QuestionCount, i.IsActive))
            .ToListAsync(ct);
        int users = await db.Users.AsNoTracking().CountAsync(ct);
        int blocked = await db.Users.AsNoTracking().CountAsync(u => u.IsBlocked, ct);

        // ReceivedAt is the server's clock; AnsweredAt comes from the client and could be set to anything.
        int answers = await db.Answers.AsNoTracking().CountAsync(a => a.ReceivedAt >= since, ct);
        return new AdminOverview(true, imports, users, blocked, answers);
    }

    private static AdminOverview Unreachable() => new(false, [], 0, 0, 0);

    private static string? NormalizeSearch(string? search)
    {
        string trimmed = search?.Trim() ?? string.Empty;
        if (trimmed.Length > AdminLimits.MaxSearchLength)
        {
            trimmed = trimmed[..AdminLimits.MaxSearchLength].TrimEnd();
        }

        return trimmed.Length == 0 ? null : trimmed;
    }

    private static IQueryable<UserEntity> Filter(IQueryable<UserEntity> users, string? term)
    {
        if (term is null)
        {
            return users;
        }

        string pattern = "%" + EscapeLike(term) + "%";
        if (long.TryParse(term, NumberStyles.None, CultureInfo.InvariantCulture, out long telegramUserId))
        {
            return users.Where(u => u.TelegramUserId == telegramUserId
                || EF.Functions.ILike(EF.Functions.Collate(u.DisplayName!, Collations.IcuSearch), pattern, LikeEscape)
                || EF.Functions.ILike(EF.Functions.Collate(u.Username!, Collations.IcuSearch), pattern, LikeEscape));
        }

        // Names are often Cyrillic: the explicit ICU collation keeps the match case-insensitive on any database ctype.
        return users.Where(u => EF.Functions.ILike(EF.Functions.Collate(u.DisplayName!, Collations.IcuSearch), pattern, LikeEscape)
            || EF.Functions.ILike(EF.Functions.Collate(u.Username!, Collations.IcuSearch), pattern, LikeEscape));
    }

    // The characters ILIKE treats as pattern syntax become literals, so a search for "100%" finds only that text.
    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private AdminUserRow ToRow(UserEntity user, IEnumerable<string> stored)
    {
        IEnumerable<string> all = _adminIds.Contains(user.TelegramUserId) ? stored.Append(Roles.Admin) : stored;
        string[] roles = [.. all.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        return new AdminUserRow(
            new UserId(user.Id),
            user.TelegramUserId,
            user.DisplayName,
            user.Username,
            user.CreatedAt,
            user.LastSignInAt,
            user.IsBlocked,
            roles);
    }
}
