namespace Trainer.Infrastructure.Persistence.Entities;

internal sealed class QuestionLocaleEntity
{
    public required string QuestionId { get; set; }
    public required string Culture { get; set; }
    public required string Title { get; set; }
    public required string PromptHtml { get; set; }
    public required string AnswerHtml { get; set; }
    public required string ExplanationHtml { get; set; }
    public string? HintHtml { get; set; }

    /// <summary>JSON array of <c>LocaleOption</c>.</summary>
    public required string Options { get; set; }

    /// <summary>JSON array of <c>LocaleLink</c>.</summary>
    public required string Links { get; set; }
}
