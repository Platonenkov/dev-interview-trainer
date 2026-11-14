namespace Trainer.Application.Admin;

/// <summary>Read side of the admin page. Callers authorise first; nothing here checks who is asking.</summary>
public interface IAdminQueries
{
    /// <summary>Database reachability, the last <see cref="AdminLimits.RecentImportCount"/> imports and the user and answer counts.</summary>
    Task<AdminOverview> GetOverviewAsync(CancellationToken ct);

    /// <summary>
    /// Users by most recent sign-in, <see cref="AdminLimits.UsersPerPage"/> per page. <paramref name="search"/> (trimmed;
    /// null or blank means no filter) matches a Telegram user id exactly when it is a number, or any case-insensitive
    /// substring of the display name or username; <c>%</c>, <c>_</c> and <c>\</c> in it are literal. A page outside
    /// the range is clamped.
    /// </summary>
    Task<UserPage> GetUsersAsync(string? search, int page, CancellationToken ct);
}
