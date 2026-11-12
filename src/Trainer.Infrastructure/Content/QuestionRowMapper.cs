using System.Text.Json;
using Trainer.Content.Model;
using Trainer.Content.Rendering;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;
using Trainer.Infrastructure.Persistence.Entities;

namespace Trainer.Infrastructure.Content;

/// <summary>Turns validated question documents into rows, rendering Markdown and code to HTML on the way.</summary>
internal sealed class QuestionRowMapper(IContentRenderer renderer)
{
    private const string FallbackCodeLanguage = "csharp";

    /// <summary>Fills <paramref name="entity"/> from the document. The document must have passed validation.</summary>
    public void Apply(QuestionEntity entity, QuestionDocument document)
    {
        entity.StackKey = document.Stack;
        entity.TopicKey = document.Topic;
        entity.Slug = document.Slug;
        entity.Level = ParseKey<Level>(LevelKeys.TryParse(document.Level, out Level level), level, document);
        entity.Type = ParseKey<QuestionType>(QuestionTypeKeys.TryParse(document.Type, out QuestionType type), type, document);
        entity.Grading = ParseKey<GradingMode>(GradingModeKeys.TryParse(document.Grading, out GradingMode grading), grading, document);
        entity.Tags = [.. document.Tags];
        entity.Payload = JsonSerializer.Serialize(BuildPayload(document), PayloadJsonContext.Default.QuestionPayload);
        entity.Verified = document.Meta.Verified;
        entity.DotnetVersion = document.Meta.DotnetVersion;
    }

    public List<QuestionLocaleEntity> BuildLocales(QuestionDocument document, string questionId)
    {
        List<QuestionLocaleEntity> rows = new(document.Locales.Count);
        foreach ((string culture, QuestionLocaleDocument locale) in document.Locales)
        {
            rows.Add(new QuestionLocaleEntity
            {
                QuestionId = questionId,
                Culture = culture,
                Title = locale.Title,
                PromptHtml = renderer.RenderMarkdown(locale.Prompt),
                AnswerHtml = renderer.RenderMarkdown(locale.Answer),
                ExplanationHtml = renderer.RenderMarkdown(locale.Explanation),
                HintHtml = locale.Hint is null ? null : renderer.RenderMarkdown(locale.Hint),
                Options = JsonSerializer.Serialize(BuildOptions(document, locale), PayloadJsonContext.Default.IReadOnlyListLocaleOption),
                Links = JsonSerializer.Serialize(BuildLinks(document, locale), PayloadJsonContext.Default.IReadOnlyListLocaleLink),
            });
        }

        return rows;
    }

    private static T ParseKey<T>(bool parsed, T value, QuestionDocument document)
        where T : struct, Enum =>
        parsed ? value : throw new InvalidOperationException($"Question {document.Id} has an invalid {typeof(T).Name} key; content must be validated before mapping.");

    private QuestionPayload BuildPayload(QuestionDocument document)
    {
        CodeBlock? code = document.Code;
        return new QuestionPayload
        {
            CodeLanguage = code?.Language,
            Harness = code?.Harness,
            CodeSource = code?.Source,
            CodeHtml = code is null ? null : renderer.RenderCode(code.Source, code.Language),
            Expect = document.Expect,
            AcceptedOutputs = document.AcceptedOutputs,
            BugLineSets = document.BugLineSets,
            FixedSource = document.FixedSource,
            FixedCodeHtml = document.FixedSource is null ? null : renderer.RenderCode(document.FixedSource, code?.Language ?? FallbackCodeLanguage),
            FixedExpect = document.FixedExpect,
            Options = document.Options?.Select(o => new PayloadOption(o.Id, o.Correct, o.Expect)).ToList(),
        };
    }

    private List<LocaleOption> BuildOptions(QuestionDocument document, QuestionLocaleDocument locale)
    {
        List<LocaleOption> options = [];
        foreach (OptionDocument option in document.Options ?? [])
        {
            if (locale.Options is null || !locale.Options.TryGetValue(option.Id, out string? text))
            {
                continue;
            }

            string? explanation = null;
            if (locale.OptionExplanations is not null && locale.OptionExplanations.TryGetValue(option.Id, out string? explanationText))
            {
                explanation = renderer.RenderMarkdown(explanationText);
            }

            options.Add(new LocaleOption(option.Id, renderer.RenderMarkdown(text), explanation));
        }

        return options;
    }

    private static List<LocaleLink> BuildLinks(QuestionDocument document, QuestionLocaleDocument locale)
    {
        List<LocaleLink> links = new(document.Links.Count);
        for (int i = 0; i < document.Links.Count; i++)
        {
            LinkDocument link = document.Links[i];
            string title = i < locale.LinkTitles.Count ? locale.LinkTitles[i] : link.Url;
            links.Add(new LocaleLink(link.Url, link.Lang, link.Kind, title));
        }

        return links;
    }
}
