using System.Text.Json.Serialization;
using Trainer.Content.Model;

namespace Trainer.Infrastructure.Content;

/// <summary>Language-neutral part of a question, stored in <c>questions.payload</c>. Changing its shape requires a render version bump.</summary>
internal sealed record QuestionPayload
{
    public string? CodeLanguage { get; init; }
    public string? Harness { get; init; }
    public string? CodeSource { get; init; }
    public string? CodeHtml { get; init; }

    /// <summary>The per-line <c>span.line</c> fragments whose newline join is the body of <see cref="CodeHtml"/>; the practice client marks bug lines on them.</summary>
    public IReadOnlyList<string>? CodeLinesHtml { get; init; }

    public Expectation? Expect { get; init; }
    public IReadOnlyList<string>? AcceptedOutputs { get; init; }
    public IReadOnlyList<IReadOnlyList<int>>? BugLineSets { get; init; }
    public string? FixedSource { get; init; }
    public string? FixedCodeHtml { get; init; }
    public Expectation? FixedExpect { get; init; }
    public IReadOnlyList<PayloadOption>? Options { get; init; }
}

internal sealed record PayloadOption(string Id, bool Correct, Expectation? Expect);

/// <summary>Localised answer option, stored in <c>question_locales.options</c>; texts are rendered HTML.</summary>
internal sealed record LocaleOption(string Id, string TextHtml, string? ExplanationHtml);

/// <summary>Source link, stored in <c>question_locales.links</c>; <see cref="Lang"/> is the language of the linked page.</summary>
internal sealed record LocaleLink(string Url, string Lang, string Kind, string Title);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(QuestionPayload))]
[JsonSerializable(typeof(IReadOnlyList<LocaleOption>))]
[JsonSerializable(typeof(IReadOnlyList<LocaleLink>))]
internal sealed partial class PayloadJsonContext : JsonSerializerContext;
