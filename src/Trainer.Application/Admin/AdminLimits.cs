namespace Trainer.Application.Admin;

/// <summary>Sizes the admin queries use, shared so tests and the page agree with them.</summary>
public static class AdminLimits
{
    public const int UsersPerPage = 50;
    public const int RecentImportCount = 10;

    /// <summary>A longer search text is cut, so one request cannot send an arbitrarily large pattern to the database.</summary>
    public const int MaxSearchLength = 100;
}
