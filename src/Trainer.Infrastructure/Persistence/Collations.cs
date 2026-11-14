namespace Trainer.Infrastructure.Persistence;

/// <summary>Collations named explicitly in queries.</summary>
internal static class Collations
{
    /// <summary>
    /// ICU root collation for case-insensitive search. <c>ILIKE</c> folds case with the database's ctype, which silently
    /// becomes ASCII-only on a database created with <c>LC_CTYPE C</c>; this collation folds every script the same way on
    /// any database (deterministic, so <c>LIKE</c> still works), and a server without ICU fails the query loudly instead of
    /// mismatching Cyrillic.
    /// </summary>
    public const string IcuSearch = "und-x-icu";
}
