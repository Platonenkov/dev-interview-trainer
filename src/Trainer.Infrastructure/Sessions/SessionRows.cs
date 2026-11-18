using System.Text.Json;
using Trainer.Contracts;
using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;
using Trainer.Infrastructure.Content;
using Trainer.Infrastructure.Persistence;
using Trainer.Infrastructure.Persistence.Entities;
using Trainer.Infrastructure.Queries;

namespace Trainer.Infrastructure.Sessions;

/// <summary>A question joined with its locale for the culture; <see cref="DueAt"/> is set only by the review query.</summary>
internal sealed class SessionCandidate
{
    public required QuestionEntity Question { get; init; }
    public required QuestionLocaleEntity Locale { get; init; }
    public DateTimeOffset? DueAt { get; init; }
}

/// <summary>The columns a session needs, fetched in one query; the payload and the localised JSON are parsed afterwards.</summary>
internal sealed record SessionRow(
    string Id,
    string Slug,
    string TopicKey,
    Level Level,
    QuestionType Type,
    string Payload,
    string Title,
    string PromptHtml,
    string? HintHtml,
    string OptionsJson,
    string AnswerHtml,
    string ExplanationHtml,
    string LinksJson);

/// <summary>Builds the wire DTO of a question from the stored row.</summary>
internal static class SessionRows
{
    public static readonly System.Linq.Expressions.Expression<Func<SessionCandidate, SessionRow>> Project = c => new SessionRow(
        c.Question.Id,
        c.Question.Slug,
        c.Question.TopicKey,
        c.Question.Level,
        c.Question.Type,
        c.Question.Payload,
        c.Locale.Title,
        c.Locale.PromptHtml,
        c.Locale.HintHtml,
        c.Locale.Options,
        c.Locale.AnswerHtml,
        c.Locale.ExplanationHtml,
        c.Locale.Links);

    /// <summary>Joins questions with their locale for the culture; a question without one drops out.</summary>
    public static IQueryable<SessionCandidate> Candidates(TrainerDbContext db, IQueryable<QuestionEntity> questions, CultureKey culture)
    {
        string cultureValue = culture.Value;
        return questions.Join(
            db.QuestionLocales.Where(l => l.Culture == cultureValue),
            q => q.Id,
            l => l.QuestionId,
            (q, l) => new SessionCandidate { Question = q, Locale = l });
    }

    public static QuestionDto ToDto(SessionRow row, IReadOnlyDictionary<string, string> topicTitles)
    {
        QuestionPayload payload = JsonSerializer.Deserialize(row.Payload, PayloadJsonContext.Default.QuestionPayload) ?? new QuestionPayload();
        Dictionary<string, bool> correct = (payload.Options ?? []).ToDictionary(o => o.Id, o => o.Correct, StringComparer.Ordinal);
        IReadOnlyList<LocaleOption> options = JsonSerializer.Deserialize(row.OptionsJson, PayloadJsonContext.Default.IReadOnlyListLocaleOption) ?? [];
        IReadOnlyList<LocaleLink> links = JsonSerializer.Deserialize(row.LinksJson, PayloadJsonContext.Default.IReadOnlyListLocaleLink) ?? [];

        return new QuestionDto(
            row.Id,
            row.Slug,
            row.TopicKey,
            LiveQuestions.TitleOf(topicTitles, row.TopicKey),
            LevelKeys.ToKey(row.Level),
            QuestionTypeKeys.ToKey(row.Type),
            row.Title,
            row.PromptHtml,
            row.HintHtml,
            payload.CodeLinesHtml,
            payload.FixedCodeHtml,
            [.. options.Select(o => new OptionDto(o.Id, o.TextHtml, correct.GetValueOrDefault(o.Id), o.ExplanationHtml))],
            payload.AcceptedOutputs,
            payload.BugLineSets,
            row.AnswerHtml,
            row.ExplanationHtml,
            [.. links.Select(l => new LinkDto(l.Url, l.Title, l.Lang))]);
    }
}
