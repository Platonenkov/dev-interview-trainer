namespace Trainer.Contracts;

/// <summary>
/// A question as the practice islands receive it. Every <c>*Html</c> member is rendered and sanitised at import time and
/// may be written to the page as markup; nothing else may. Keys (level, type, topic) are the wire keys of the content
/// format. <paramref name="Options"/> carry <see cref="OptionDto.Correct"/> because the client grades and explains locally,
/// so a session also works offline.
/// </summary>
/// <param name="CodeLinesHtml">One self-contained line fragment per source line, so a find-bug question can mark lines.</param>
/// <param name="BugLineSets">Accepted sets of 1-based buggy line numbers (find-bug only).</param>
public sealed record QuestionDto(
    string Id,
    string Slug,
    string Topic,
    string TopicTitle,
    string Level,
    string Type,
    string Title,
    string PromptHtml,
    string? HintHtml,
    IReadOnlyList<string>? CodeLinesHtml,
    string? FixedCodeHtml,
    IReadOnlyList<OptionDto> Options,
    IReadOnlyList<string>? AcceptedOutputs,
    IReadOnlyList<IReadOnlyList<int>>? BugLineSets,
    string AnswerHtml,
    string ExplanationHtml,
    IReadOnlyList<LinkDto> Links);

/// <summary>An answer option. The text and explanation are localised, <paramref name="Correct"/> is not.</summary>
public sealed record OptionDto(string Id, string TextHtml, bool Correct, string? ExplanationHtml);

/// <summary><paramref name="Lang"/> is the language of the linked page, not of the site.</summary>
public sealed record LinkDto(string Url, string Title, string Lang);
