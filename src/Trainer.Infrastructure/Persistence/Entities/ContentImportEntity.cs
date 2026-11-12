namespace Trainer.Infrastructure.Persistence.Entities;

internal sealed class ContentImportEntity
{
    public long Id { get; set; }
    public required string Hash { get; set; }
    public required int SchemaVersion { get; set; }
    public required int RenderVersion { get; set; }
    public required DateTimeOffset ImportedAt { get; set; }
    public required int QuestionCount { get; set; }
    public required bool IsActive { get; set; }
}
