namespace Trainer.Application.Content;

public enum ContentImportStatus
{
    /// <summary>The content was written and is now the active import.</summary>
    Imported,

    /// <summary>The active import already has this content hash and render version; nothing was written.</summary>
    Unchanged,

    /// <summary>The content failed validation; nothing was written.</summary>
    Rejected,
}

/// <param name="Status">Outcome of the import.</param>
/// <param name="Hash">Content hash of the files that were read.</param>
/// <param name="Imported">Number of questions written (zero unless <see cref="ContentImportStatus.Imported"/>).</param>
/// <param name="Retired">Number of questions newly retired because their files are gone.</param>
/// <param name="Issues">Validation problems; empty unless <see cref="ContentImportStatus.Rejected"/>.</param>
public sealed record ContentImportResult(
    ContentImportStatus Status,
    string Hash,
    int Imported,
    int Retired,
    IReadOnlyList<ImportIssue> Issues);
