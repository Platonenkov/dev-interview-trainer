using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;

namespace Trainer.Application.Questions;

/// <summary>
/// Everything the question page shows. All <c>*Html</c> members are rendered and sanitised at import time and may
/// be written to the page as markup; nothing else in this model may. <paramref name="TopicIsRetired"/> is true when the
/// topic page no longer exists, so the question page must not link to it.
/// </summary>
public sealed record QuestionPageModel(
    QuestionId Id,
    string Slug,
    StackKey Stack,
    TopicKey Topic,
    string TopicTitle,
    Level Level,
    QuestionType Type,
    string Title,
    string PromptHtml,
    string? HintHtml,
    string? CodeHtml,
    string? FixedCodeHtml,
    IReadOnlyList<OptionView> Options,
    string AnswerHtml,
    string ExplanationHtml,
    IReadOnlyList<LinkView> Links,
    IReadOnlyList<CultureKey> AvailableCultures,
    bool IsRetired,
    DateOnly Verified,
    string? DotnetVersion,
    bool TopicIsRetired);

/// <summary>An answer option as displayed; <paramref name="Correct"/> is language-neutral, the texts are localised.</summary>
public sealed record OptionView(string Id, string TextHtml, bool Correct, string? ExplanationHtml);

/// <summary><paramref name="Lang"/> is the language of the linked page, not of the site.</summary>
public sealed record LinkView(string Url, string Title, string Lang, string Kind);
