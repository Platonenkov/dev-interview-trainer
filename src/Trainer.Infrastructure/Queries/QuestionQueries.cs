using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Trainer.Application.Catalog;
using Trainer.Application.Questions;
using Trainer.Domain.Keys;
using Trainer.Domain.Questions;
using Trainer.Infrastructure.Content;
using Trainer.Infrastructure.Persistence;
using Trainer.Infrastructure.Persistence.Entities;

namespace Trainer.Infrastructure.Queries;

internal sealed class QuestionQueries(TrainerDbContext db) : IQuestionQueries
{
    public async Task<QuestionPageModel?> GetAsync(CultureKey culture, StackKey stack, Slug slug, CancellationToken ct)
    {
        string stackValue = stack.Value;
        string slugValue = slug.Value;
        var found = await db.Questions
            .AsNoTracking()
            .Where(q => q.StackKey == stackValue && q.Slug == slugValue)
            .Select(q => new
            {
                Question = q,
                TopicIsRetired = db.Topics.Any(t => t.StackKey == q.StackKey && t.Key == q.TopicKey && t.RetiredAt != null),
            })
            .FirstOrDefaultAsync(ct);
        if (found is null)
        {
            return null;
        }

        QuestionEntity question = found.Question;

        List<QuestionLocaleEntity> locales = await db.QuestionLocales
            .AsNoTracking()
            .Where(l => l.QuestionId == question.Id)
            .ToListAsync(ct);
        QuestionLocaleEntity? locale = locales.Find(l => string.Equals(l.Culture, culture.Value, StringComparison.Ordinal));
        if (locale is null)
        {
            return null;
        }

        Dictionary<string, string> topicTitles = await LiveQuestions.TopicTitlesAsync(db, culture, stack, ct);
        QuestionPayload payload = JsonSerializer.Deserialize(question.Payload, PayloadJsonContext.Default.QuestionPayload) ?? new QuestionPayload();
        return Map(question, locale, payload, locales, LiveQuestions.TitleOf(topicTitles, question.TopicKey), found.TopicIsRetired);
    }

    public async Task<Slug?> GetRedirectAsync(StackKey stack, Slug from, CancellationToken ct)
    {
        string stackValue = stack.Value;
        string fromValue = from.Value;
        string? target = await db.Redirects
            .AsNoTracking()
            .Where(r => r.StackKey == stackValue && r.FromSlug == fromValue)
            .Select(r => r.ToSlug)
            .FirstOrDefaultAsync(ct);
        return Slug.TryCreate(target, out Slug to) ? to : null;
    }

    public async Task<IReadOnlyList<CatalogRow>> GetRelatedAsync(
        CultureKey culture, StackKey stack, QuestionId id, int limit, CancellationToken ct)
    {
        if (limit <= 0)
        {
            return [];
        }

        string idValue = id.Value;
        string stackValue = stack.Value;
        string cultureValue = culture.Value;
        var own = await db.Questions
            .AsNoTracking()
            .Where(q => q.Id == idValue && q.StackKey == stackValue)
            .Select(q => new { q.Tags, q.TopicKey })
            .FirstOrDefaultAsync(ct);
        if (own is null || own.Tags.Length == 0)
        {
            return [];
        }

        string[] tags = own.Tags;
        string ownTopic = own.TopicKey;

        // Liveness and the culture's locale come from LiveQuestions.Of, the same rule the catalog uses, so a related
        // link can never point to a retired question or topic or to a page that would answer 404.
        var projected = await (
                from q in LiveQuestions.Of(db, culture, stack)
                where q.Id != idValue
                let shared = q.Tags.Count(t => tags.Contains(t))
                where shared > 0
                join l in db.QuestionLocales on q.Id equals l.QuestionId
                where l.Culture == cultureValue
                orderby shared descending, q.TopicKey == ownTopic ? 0 : 1, l.Title, q.Slug
                select new { q.Id, q.Slug, l.Title, q.Level, q.TopicKey, q.Type, q.Tags })
            .Take(limit)
            .ToListAsync(ct);

        Dictionary<string, string> topicTitles = await LiveQuestions.TopicTitlesAsync(db, culture, stack, ct);
        return [.. projected.Select(r => new CatalogRow(
            r.Slug, r.Title, r.Level, new TopicKey(r.TopicKey), LiveQuestions.TitleOf(topicTitles, r.TopicKey), r.Type, r.Tags,
            new QuestionId(r.Id)))];
    }

    private static QuestionPageModel Map(
        QuestionEntity question,
        QuestionLocaleEntity locale,
        QuestionPayload payload,
        List<QuestionLocaleEntity> locales,
        string topicTitle,
        bool topicIsRetired)
    {
        Dictionary<string, bool> correct = (payload.Options ?? []).ToDictionary(o => o.Id, o => o.Correct, StringComparer.Ordinal);
        IReadOnlyList<LocaleOption> localeOptions = JsonSerializer.Deserialize(locale.Options, PayloadJsonContext.Default.IReadOnlyListLocaleOption) ?? [];
        IReadOnlyList<LocaleLink> localeLinks = JsonSerializer.Deserialize(locale.Links, PayloadJsonContext.Default.IReadOnlyListLocaleLink) ?? [];
        CultureKey[] available = [.. CultureKey.Supported.Where(c => locales.Exists(l => string.Equals(l.Culture, c.Value, StringComparison.Ordinal)))];

        return new QuestionPageModel(
            new QuestionId(question.Id),
            question.Slug,
            new StackKey(question.StackKey),
            new TopicKey(question.TopicKey),
            topicTitle,
            question.Level,
            question.Type,
            locale.Title,
            locale.PromptHtml,
            locale.HintHtml,
            payload.CodeHtml,
            payload.FixedCodeHtml,
            [.. localeOptions.Select(o => new OptionView(o.Id, o.TextHtml, correct.GetValueOrDefault(o.Id), o.ExplanationHtml))],
            locale.AnswerHtml,
            locale.ExplanationHtml,
            [.. localeLinks.Select(l => new LinkView(l.Url, l.Title, l.Lang, l.Kind))],
            available,
            question.RetiredAt is not null,
            question.Verified,
            question.DotnetVersion,
            topicIsRetired);
    }
}
