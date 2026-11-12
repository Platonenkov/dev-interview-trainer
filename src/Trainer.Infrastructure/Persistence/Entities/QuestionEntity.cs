using Trainer.Domain.Levels;
using Trainer.Domain.Questions;

namespace Trainer.Infrastructure.Persistence.Entities;

/// <summary>Created and filled by the importer, which assigns every column through <c>QuestionRowMapper.Apply</c>.</summary>
internal sealed class QuestionEntity
{
    public required string Id { get; set; }
    public string StackKey { get; set; } = "";
    public string TopicKey { get; set; } = "";
    public string Slug { get; set; } = "";
    public Level Level { get; set; }
    public QuestionType Type { get; set; }
    public GradingMode Grading { get; set; }
    public string[] Tags { get; set; } = [];

    /// <summary>Language-neutral JSON, see <c>QuestionPayload</c>.</summary>
    public string Payload { get; set; } = "{}";

    public DateOnly Verified { get; set; }
    public string? DotnetVersion { get; set; }

    /// <summary>Set when the question file disappeared from the content; the question stays readable but leaves the catalog.</summary>
    public DateTimeOffset? RetiredAt { get; set; }
}
