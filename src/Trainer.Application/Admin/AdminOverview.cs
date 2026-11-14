namespace Trainer.Application.Admin;

/// <summary>One content import as the admin page lists it; <paramref name="Hash"/> is the full content hash.</summary>
public sealed record ImportRow(
    string Hash,
    int SchemaVersion,
    int RenderVersion,
    DateTimeOffset ImportedAt,
    int QuestionCount,
    bool IsActive);

/// <summary>
/// The numbers on top of the admin page. When the database cannot be reached, <paramref name="DatabaseReachable"/> is
/// false and the rest is empty or zero.
/// </summary>
public sealed record AdminOverview(
    bool DatabaseReachable,
    IReadOnlyList<ImportRow> RecentImports,
    int UserCount,
    int BlockedCount,
    int AnswersLast24Hours);
