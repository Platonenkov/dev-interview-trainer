using System.Globalization;
using Trainer.Content.Model;
using Trainer.Content.Reading;
using Trainer.Content.Rendering;
using Trainer.Content.Schema;
using Trainer.Domain.Grading;
using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;

namespace Trainer.Content.Validation;

/// <summary>
/// Semantic validation of a <see cref="ContentSet"/>. The class holds no state between calls, so one instance
/// can be shared. Rules are grouped one private method per concern; every violation is collected.
/// </summary>
public sealed class ContentValidator : IContentValidator
{
    private const string StacksPath = "stacks.yaml";
    private const string SchemaVersionPath = "schema-version";

    private readonly IContentRenderer _renderer;
    private readonly TimeProvider _time;

    public ContentValidator(IContentRenderer renderer, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(time);
        _renderer = renderer;
        _time = time;
    }

    public IReadOnlyList<ContentIssue> Validate(ContentSet content)
    {
        ArgumentNullException.ThrowIfNull(content);

        List<ContentIssue> issues = [];
        CheckSchemaVersion(content, issues);
        CheckStacks(content, issues);
        CheckTopics(content, issues);
        CheckIdentity(content, issues);
        CheckPlacement(content, issues);

        DateOnly today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        foreach (SourcedQuestion question in content.Questions)
        {
            CheckQuestion(question, today, issues);
        }

        CheckRedirects(content, issues);
        return issues;
    }

    private static void CheckSchemaVersion(ContentSet set, List<ContentIssue> issues)
    {
        if (set.SchemaVersion < ContentSchema.MinSupported || set.SchemaVersion > ContentSchema.MaxSupported)
        {
            Add(issues, SchemaVersionPath, IssueCodes.SchemaVersionUnsupported,
                $"Schema version {set.SchemaVersion} is outside the supported range {ContentSchema.MinSupported}..{ContentSchema.MaxSupported}.");
        }
    }

    // The static edition serves question files at data/{culture}/q/..., so a stack named like that segment would shadow its own index.
    private static readonly string[] ReservedStackKeys = ["q"];

    private static void CheckStacks(ContentSet set, List<ContentIssue> issues)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int i = 0; i < set.Stacks.Count; i++)
        {
            StackDocument stack = set.Stacks[i];
            if (!seen.Add(stack.Key))
            {
                Add(issues, StacksPath, IssueCodes.TaxonomyDuplicate, $"Stack '{stack.Key}' is declared more than once.", $"[{i}].key");
            }

            if (ReservedStackKeys.Contains(stack.Key, StringComparer.Ordinal))
            {
                Add(issues, StacksPath, IssueCodes.StackKeyReserved, $"Stack key '{stack.Key}' is reserved for a site route; choose another key.", $"[{i}].key");
            }

            CheckTaxonomyLocales(StacksPath, $"[{i}]", $"Stack '{stack.Key}'", stack.Locales, issues);
        }
    }

    private static void CheckTopics(ContentSet set, List<ContentIssue> issues)
    {
        HashSet<string> knownStacks = StackKeys(set);
        foreach ((string stack, IReadOnlyList<TopicDocument> topics) in set.TopicsByStack)
        {
            string path = $"{stack}/topics.yaml";
            if (!knownStacks.Contains(stack))
            {
                Add(issues, path, IssueCodes.StackUnknown, $"Topics are declared for stack '{stack}', which is not in {StacksPath}.");
            }

            HashSet<string> seen = new(StringComparer.Ordinal);
            for (int i = 0; i < topics.Count; i++)
            {
                TopicDocument topic = topics[i];
                if (!seen.Add(topic.Key))
                {
                    Add(issues, path, IssueCodes.TaxonomyDuplicate, $"Topic '{topic.Key}' is declared more than once.", $"[{i}].key");
                }

                CheckTaxonomyLocales(path, $"[{i}]", $"Topic '{topic.Key}'", topic.Locales, issues);
            }
        }
    }

    private static void CheckTaxonomyLocales(
        string path, string jsonPath, string subject, IReadOnlyDictionary<string, LocaleTextDocument> locales, List<ContentIssue> issues)
    {
        foreach (CultureKey culture in CultureKey.Supported)
        {
            if (!locales.TryGetValue(culture.Value, out LocaleTextDocument? text)
                || string.IsNullOrWhiteSpace(text.Title)
                || string.IsNullOrWhiteSpace(text.Description))
            {
                Add(issues, path, IssueCodes.TaxonomyLocaleIncomplete,
                    $"{subject} needs a non-empty title and description in culture '{culture.Value}'.", $"{jsonPath}.locales.{culture.Value}");
            }
        }

        foreach (string locale in locales.Keys.Where(k => !CultureKey.TryCreate(k, out _)))
        {
            Add(issues, path, IssueCodes.LocaleUnsupported, $"{subject} has unsupported locale '{locale}'.", $"{jsonPath}.locales.{locale}");
        }
    }

    private static void CheckIdentity(ContentSet set, List<ContentIssue> issues)
    {
        Dictionary<string, string> idOwners = new(StringComparer.Ordinal);
        Dictionary<(string Stack, string Slug), string> slugOwners = [];
        foreach (SourcedQuestion item in set.Questions)
        {
            QuestionDocument d = item.Document;
            if (!QuestionId.TryCreate(d.Id, out _))
            {
                Add(issues, item.RelativePath, IssueCodes.IdInvalid, $"'{d.Id}' is not a valid ULID.", "id");
            }
            else if (!idOwners.TryAdd(d.Id, item.RelativePath))
            {
                Add(issues, item.RelativePath, IssueCodes.IdDuplicate, $"Id '{d.Id}' is already used by {idOwners[d.Id]}.", "id");
            }

            if (!Slug.TryCreate(d.Slug, out _))
            {
                Add(issues, item.RelativePath, IssueCodes.SlugInvalid, $"'{d.Slug}' is not a valid slug.", "slug");
            }
            else if (!slugOwners.TryAdd((d.Stack, d.Slug), item.RelativePath))
            {
                Add(issues, item.RelativePath, IssueCodes.SlugDuplicate,
                    $"Slug '{d.Slug}' is already used in stack '{d.Stack}' by {slugOwners[(d.Stack, d.Slug)]}.", "slug");
            }
        }
    }

    private static void CheckPlacement(ContentSet set, List<ContentIssue> issues)
    {
        HashSet<string> knownStacks = StackKeys(set);
        foreach (SourcedQuestion item in set.Questions)
        {
            QuestionDocument d = item.Document;
            string expectedPath = $"{d.Stack}/{d.Topic}/{d.Slug}.yaml";
            if (!string.Equals(item.RelativePath, expectedPath, StringComparison.Ordinal))
            {
                Add(issues, item.RelativePath, IssueCodes.PathMismatch, $"The file must be stored at '{expectedPath}'.");
            }

            if (!knownStacks.Contains(d.Stack))
            {
                Add(issues, item.RelativePath, IssueCodes.StackUnknown, $"Stack '{d.Stack}' is not in {StacksPath}.", "stack");
            }
            else if (!set.TopicsByStack.TryGetValue(d.Stack, out IReadOnlyList<TopicDocument>? topics)
                     || topics.All(t => !string.Equals(t.Key, d.Topic, StringComparison.Ordinal)))
            {
                Add(issues, item.RelativePath, IssueCodes.TopicUnknown, $"Topic '{d.Topic}' is not declared for stack '{d.Stack}'.", "topic");
            }
        }
    }

    private void CheckQuestion(SourcedQuestion item, DateOnly today, List<ContentIssue> issues)
    {
        QuestionDocument d = item.Document;
        string path = item.RelativePath;

        bool typeKnown = TryParseType(d.Type, out QuestionType type);
        CheckClassification(path, d, typeKnown, type, issues);
        CheckTags(path, d, issues);
        CheckOptions(path, d, typeKnown, type, issues);
        CheckOutput(path, d, typeKnown, type, issues);
        CheckExpectations(path, d, issues);
        CheckCode(path, d, typeKnown, type, issues);
        CheckBugLines(path, d, issues);
        CheckFixed(path, d, issues);
        CheckLinks(path, d, issues);
        CheckLocales(path, d, issues);

        if (d.Meta.Verified > today)
        {
            Add(issues, path, IssueCodes.VerifiedInFuture, $"meta.verified ({d.Meta.Verified:yyyy-MM-dd}) is in the future.", "meta.verified");
        }
    }

    private static void CheckClassification(string path, QuestionDocument d, bool typeKnown, QuestionType type, List<ContentIssue> issues)
    {
        if (!LevelKeys.All.Any(l => string.Equals(LevelKeys.ToKey(l), d.Level, StringComparison.Ordinal)))
        {
            Add(issues, path, IssueCodes.LevelInvalid, $"Level '{d.Level}' is not one of the allowed levels.", "level");
        }

        if (!typeKnown)
        {
            Add(issues, path, IssueCodes.TypeInvalid, $"Type '{d.Type}' is not one of the allowed question types.", "type");
        }

        GradingMode? grading = GradingModeKeys.All
            .Where(g => string.Equals(GradingModeKeys.ToKey(g), d.Grading, StringComparison.Ordinal))
            .Select(g => (GradingMode?)g)
            .FirstOrDefault();
        if (grading is null)
        {
            Add(issues, path, IssueCodes.GradingInvalid, $"Grading '{d.Grading}' is not one of the allowed grading modes.", "grading");
        }
        else if (typeKnown)
        {
            GradingMode expected = type is QuestionType.Open or QuestionType.Situation ? GradingMode.Self : GradingMode.Auto;
            if (grading.Value != expected)
            {
                Add(issues, path, IssueCodes.GradingTypeMismatch,
                    $"Type '{d.Type}' requires grading '{GradingModeKeys.ToKey(expected)}'.", "grading");
            }
        }
    }

    private static void CheckTags(string path, QuestionDocument d, List<ContentIssue> issues)
    {
        for (int i = 0; i < d.Tags.Count; i++)
        {
            if (!Slug.TryCreate(d.Tags[i], out _))
            {
                Add(issues, path, IssueCodes.TagInvalid, $"Tag '{d.Tags[i]}' must be lowercase kebab-case.", $"tags[{i}]");
            }
        }
    }

    private static void CheckOptions(string path, QuestionDocument d, bool typeKnown, QuestionType type, List<ContentIssue> issues)
    {
        IReadOnlyList<OptionDocument> options = d.Options ?? [];
        int correct = options.Count(o => o.Correct);

        if (typeKnown)
        {
            CheckOptionCounts(path, d, type, options.Count, correct, issues);
        }

        HashSet<string> ids = new(StringComparer.Ordinal);
        for (int i = 0; i < options.Count; i++)
        {
            OptionDocument option = options[i];
            if (string.IsNullOrWhiteSpace(option.Id))
            {
                Add(issues, path, IssueCodes.OptionIdDuplicate, "Option ids must not be empty.", $"options[{i}].id");
            }
            else if (!ids.Add(option.Id))
            {
                Add(issues, path, IssueCodes.OptionIdDuplicate, $"Option id '{option.Id}' is used more than once.", $"options[{i}].id");
            }

            if (typeKnown && type == QuestionType.Output && option.Expect is null)
            {
                Add(issues, path, IssueCodes.OutputExpectRequired, "Every option of an 'output' question needs an expect.", $"options[{i}].expect");
            }
        }
    }

    private static void CheckOptionCounts(string path, QuestionDocument d, QuestionType type, int count, int correct, List<ContentIssue> issues)
    {
        switch (type)
        {
            case QuestionType.Single or QuestionType.Multi:
                if (count < 2)
                {
                    Add(issues, path, IssueCodes.OptionsRequired, $"A '{d.Type}' question needs at least two options.", "options");
                }
                else if (type == QuestionType.Single && correct != 1)
                {
                    Add(issues, path, IssueCodes.OptionsCorrectCount, $"A 'single' question needs exactly one correct option, found {correct}.", "options");
                }
                else if (type == QuestionType.Multi && correct < 1)
                {
                    Add(issues, path, IssueCodes.OptionsCorrectCount, "A 'multi' question needs at least one correct option.", "options");
                }

                break;
            case QuestionType.Output when count > 0 && correct != 1:
                Add(issues, path, IssueCodes.OptionsCorrectCount, $"An 'output' question with options needs exactly one correct option, found {correct}.", "options");
                break;
            case QuestionType.Open or QuestionType.Situation or QuestionType.FindBug when count > 0:
                Add(issues, path, IssueCodes.OptionsForbidden, $"A '{d.Type}' question must not have options.", "options");
                break;
        }
    }

    private static void CheckOutput(string path, QuestionDocument d, bool typeKnown, QuestionType type, List<ContentIssue> issues)
    {
        if (!typeKnown || type != QuestionType.Output)
        {
            return;
        }

        if (d.Expect is null)
        {
            Add(issues, path, IssueCodes.OutputExpectRequired, "An 'output' question needs an expect.", "expect");
            return;
        }

        if (!string.Equals(d.Expect.Kind, ContentVocabulary.ExpectKindOutput, StringComparison.Ordinal))
        {
            return;
        }

        bool hasOptions = d.Options is { Count: > 0 };
        bool hasAccepted = d.AcceptedOutputs is { Count: > 0 };
        if (!hasOptions && !hasAccepted)
        {
            Add(issues, path, IssueCodes.OutputAnswerMode, "An 'output' question without options needs acceptedOutputs to be answerable.", "acceptedOutputs");
        }

        if (hasAccepted && d.Expect.Value is not null)
        {
            string expected = OutputText.Normalise(d.Expect.Value);
            if (!d.AcceptedOutputs!.Any(a => string.Equals(OutputText.Normalise(a), expected, StringComparison.Ordinal)))
            {
                Add(issues, path, IssueCodes.AcceptedOutputsMismatch, "expect.value must be one of acceptedOutputs after trimming and LF normalisation.", "acceptedOutputs");
            }
        }
    }

    private static void CheckExpectations(string path, QuestionDocument d, List<ContentIssue> issues)
    {
        CheckExpectation(path, "expect", d.Expect, issues);
        CheckExpectation(path, "fixedExpect", d.FixedExpect, issues);
        IReadOnlyList<OptionDocument> options = d.Options ?? [];
        for (int i = 0; i < options.Count; i++)
        {
            CheckExpectation(path, $"options[{i}].expect", options[i].Expect, issues);
        }
    }

    private static void CheckExpectation(string path, string jsonPath, Expectation? expectation, List<ContentIssue> issues)
    {
        if (expectation is null)
        {
            return;
        }

        if (!ContentVocabulary.ExpectKinds.Contains(expectation.Kind, StringComparer.Ordinal))
        {
            Add(issues, path, IssueCodes.ExpectKindInvalid, $"Expect kind '{expectation.Kind}' is not allowed.", $"{jsonPath}.kind");
        }
        else if (expectation.Value is null
                 && (string.Equals(expectation.Kind, ContentVocabulary.ExpectKindOutput, StringComparison.Ordinal)
                     || string.Equals(expectation.Kind, ContentVocabulary.ExpectKindException, StringComparison.Ordinal)))
        {
            Add(issues, path, IssueCodes.ExpectValueRequired, $"Expect kind '{expectation.Kind}' needs a value.", $"{jsonPath}.value");
        }
    }

    private static void CheckCode(string path, QuestionDocument d, bool typeKnown, QuestionType type, List<ContentIssue> issues)
    {
        if (d.Code is null)
        {
            if (typeKnown && type is QuestionType.Output or QuestionType.FindBug)
            {
                Add(issues, path, IssueCodes.CodeRequired, $"A '{d.Type}' question needs code.", "code");
            }

            return;
        }

        if (!ContentVocabulary.Harnesses.Contains(d.Code.Harness, StringComparer.Ordinal))
        {
            Add(issues, path, IssueCodes.HarnessInvalid, $"Harness '{d.Code.Harness}' is not allowed.", "code.harness");
        }
        else if (string.Equals(d.Code.Harness, ContentVocabulary.HarnessNone, StringComparison.Ordinal))
        {
            if (typeKnown && type == QuestionType.Output)
            {
                Add(issues, path, IssueCodes.HarnessNoneForbidden, "An 'output' question must be runnable, so harness 'none' is not allowed.", "code.harness");
            }
            else if (string.IsNullOrWhiteSpace(d.Meta.NoRunReason))
            {
                Add(issues, path, IssueCodes.HarnessNoneReason, "Harness 'none' requires meta.noRunReason.", "meta.noRunReason");
            }
        }
    }

    private static void CheckBugLines(string path, QuestionDocument d, List<ContentIssue> issues)
    {
        bool isFindBug = string.Equals(d.Type, QuestionTypeKeys.ToKey(QuestionType.FindBug), StringComparison.Ordinal);
        if (isFindBug && (d.BugLineSets is not { Count: > 0 } || d.BugLineSets.Any(set => set.Count == 0)))
        {
            Add(issues, path, IssueCodes.BugLinesRequired, "A 'find-bug' question needs bugLineSets with at least one non-empty set.", "bugLineSets");
        }

        if (d.BugLineSets is null)
        {
            return;
        }

        int lineCount = CountLines(d.Code?.Source);
        for (int set = 0; set < d.BugLineSets.Count; set++)
        {
            for (int index = 0; index < d.BugLineSets[set].Count; index++)
            {
                int line = d.BugLineSets[set][index];
                if (line < 1 || line > lineCount)
                {
                    Add(issues, path, IssueCodes.BuglinesOutOfRange,
                        $"Bug line {line.ToString(CultureInfo.InvariantCulture)} is outside 1..{lineCount.ToString(CultureInfo.InvariantCulture)}.",
                        $"bugLineSets[{set}][{index}]");
                }
            }
        }
    }

    private static void CheckFixed(string path, QuestionDocument d, List<ContentIssue> issues)
    {
        if (d.FixedSource is not null && d.FixedExpect is null)
        {
            Add(issues, path, IssueCodes.FixedExpectRequired, "fixedSource requires fixedExpect.", "fixedExpect");
        }
    }

    private static void CheckLinks(string path, QuestionDocument d, List<ContentIssue> issues)
    {
        if (d.Links.Count == 0)
        {
            Add(issues, path, IssueCodes.LinksRequired, "A question needs at least one source link.", "links");
        }

        for (int i = 0; i < d.Links.Count; i++)
        {
            LinkDocument link = d.Links[i];
            if (!SafeLinkPolicy.IsAllowedAbsolute(link.Url))
            {
                Add(issues, path, IssueCodes.LinkScheme, $"Link '{link.Url}' must be an absolute https URL.", $"links[{i}].url");
            }

            if (!ContentVocabulary.LinkKinds.Contains(link.Kind, StringComparer.Ordinal))
            {
                Add(issues, path, IssueCodes.LinkKindInvalid, $"Link kind '{link.Kind}' is not allowed.", $"links[{i}].kind");
            }

            if (!CultureKey.TryCreate(link.Lang, out _))
            {
                Add(issues, path, IssueCodes.LinkLangInvalid, $"Link language '{link.Lang}' is not a supported culture.", $"links[{i}].lang");
            }
        }
    }

    private void CheckLocales(string path, QuestionDocument d, List<ContentIssue> issues)
    {
        if (d.Locales.Count == 0)
        {
            Add(issues, path, IssueCodes.LocaleIncomplete, "A question needs at least one locale block.", "locales");
        }

        foreach ((string culture, QuestionLocaleDocument locale) in d.Locales)
        {
            string prefix = $"locales.{culture}";
            if (!CultureKey.TryCreate(culture, out _))
            {
                Add(issues, path, IssueCodes.LocaleUnsupported, $"Locale '{culture}' is not a supported culture.", prefix);
                continue;
            }

            CheckLocaleOptions(path, d, culture, locale, issues);

            if (locale.LinkTitles.Count != d.Links.Count)
            {
                Add(issues, path, IssueCodes.LocaleIncomplete,
                    $"Locale '{culture}' has {locale.LinkTitles.Count} link titles for {d.Links.Count} links.", $"{prefix}.linkTitles");
            }

            CheckMarkdown(path, prefix, locale, issues);
        }
    }

    private static void CheckLocaleOptions(string path, QuestionDocument d, string culture, QuestionLocaleDocument locale, List<ContentIssue> issues)
    {
        string prefix = $"locales.{culture}";
        foreach (OptionDocument option in d.Options ?? [])
        {
            if (locale.Options is null || !locale.Options.TryGetValue(option.Id, out string? text) || string.IsNullOrWhiteSpace(text))
            {
                Add(issues, path, IssueCodes.LocaleIncomplete, $"Locale '{culture}' has no text for option '{option.Id}'.", $"{prefix}.options");
            }

            if (!option.Correct
                && (locale.OptionExplanations is null
                    || !locale.OptionExplanations.TryGetValue(option.Id, out string? explanation)
                    || string.IsNullOrWhiteSpace(explanation)))
            {
                Add(issues, path, IssueCodes.LocaleIncomplete, $"Locale '{culture}' has no explanation for incorrect option '{option.Id}'.", $"{prefix}.optionExplanations");
            }
        }
    }

    private void CheckMarkdown(string path, string prefix, QuestionLocaleDocument locale, List<ContentIssue> issues)
    {
        CheckMarkdownField(path, $"{prefix}.prompt", locale.Prompt, issues);
        CheckMarkdownField(path, $"{prefix}.hint", locale.Hint, issues);
        CheckMarkdownField(path, $"{prefix}.answer", locale.Answer, issues);
        CheckMarkdownField(path, $"{prefix}.explanation", locale.Explanation, issues);
        foreach ((string id, string text) in locale.Options ?? new Dictionary<string, string>())
        {
            CheckMarkdownField(path, $"{prefix}.options.{id}", text, issues);
        }

        foreach ((string id, string text) in locale.OptionExplanations ?? new Dictionary<string, string>())
        {
            CheckMarkdownField(path, $"{prefix}.optionExplanations.{id}", text, issues);
        }
    }

    private void CheckMarkdownField(string path, string jsonPath, string? markdown, List<ContentIssue> issues)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return;
        }

        foreach (string target in _renderer.FindUnsafeLinks(markdown))
        {
            Add(issues, path, IssueCodes.MarkdownLinkScheme, $"Markdown link or image target '{target}' is not allowed.", jsonPath);
        }
    }

    private static void CheckRedirects(ContentSet set, List<ContentIssue> issues)
    {
        HashSet<string> knownStacks = StackKeys(set);
        foreach ((string stack, IReadOnlyList<RedirectDocument> redirects) in set.RedirectsByStack)
        {
            string path = $"{stack}/redirects.yaml";
            if (!knownStacks.Contains(stack))
            {
                Add(issues, path, IssueCodes.StackUnknown, $"Redirects are declared for stack '{stack}', which is not in {StacksPath}.");
            }

            // Public URLs carry no topic and the importer stores redirects per slug, so everything is compared by slug.
            HashSet<string> live = set.Questions
                .Where(q => string.Equals(q.Document.Stack, stack, StringComparison.Ordinal))
                .Select(q => q.Document.Slug)
                .ToHashSet(StringComparer.Ordinal);

            Dictionary<string, string> targets = new(StringComparer.Ordinal);
            foreach (RedirectDocument redirect in redirects)
            {
                targets.TryAdd(SlugOf(redirect.From), SlugOf(redirect.To));
            }

            HashSet<string> seenSources = new(StringComparer.Ordinal);
            for (int i = 0; i < redirects.Count; i++)
            {
                RedirectDocument redirect = redirects[i];
                string from = SlugOf(redirect.From);
                string to = SlugOf(redirect.To);

                if (!seenSources.Add(from))
                {
                    Add(issues, path, IssueCodes.RedirectDuplicate, $"Slug '{from}' is already a redirect source; a slug can redirect to one target only.", $"[{i}].from");
                }

                if (live.Contains(from))
                {
                    Add(issues, path, IssueCodes.RedirectFromLive, $"'{redirect.From}' is a live question and cannot be a redirect source.", $"[{i}].from");
                }

                if (!live.Contains(to))
                {
                    Add(issues, path, IssueCodes.RedirectTargetUnknown, $"Redirect target '{redirect.To}' is not a live question.", $"[{i}].to");
                }

                if (string.Equals(from, to, StringComparison.Ordinal))
                {
                    Add(issues, path, IssueCodes.RedirectSelf, $"Redirect from '{redirect.From}' to '{redirect.To}' points at the same slug; URLs carry no topic.", $"[{i}].to");
                }
                else if (LeadsBackTo(from, to, targets))
                {
                    Add(issues, path, IssueCodes.RedirectCycle, $"Following redirects from '{redirect.From}' leads back to it.", $"[{i}].from");
                }
            }
        }
    }

    /// <summary>Redirect entries are <c>topic/slug</c> paths; the topic part is informational, only the slug is compared.</summary>
    private static string SlugOf(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static bool LeadsBackTo(string from, string first, Dictionary<string, string> targets)
    {
        HashSet<string> visited = new(StringComparer.Ordinal) { from };
        string current = first;
        while (true)
        {
            if (string.Equals(current, from, StringComparison.Ordinal))
            {
                return true;
            }

            // A loop that does not pass through the start is reported on its own members, not here.
            if (!visited.Add(current) || !targets.TryGetValue(current, out string? next))
            {
                return false;
            }

            current = next;
        }
    }

    private static bool TryParseType(string value, out QuestionType type)
    {
        // The shared parser is case-insensitive; the content format accepts only the exact lowercase keys.
        if (QuestionTypeKeys.TryParse(value, out type)
            && string.Equals(QuestionTypeKeys.ToKey(type), value, StringComparison.Ordinal))
        {
            return true;
        }

        type = default;
        return false;
    }

    private static HashSet<string> StackKeys(ContentSet set) =>
        set.Stacks.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

    private static int CountLines(string? source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return 0;
        }

        int lines = source.Count(c => c == '\n');
        return source[^1] == '\n' ? lines : lines + 1;
    }

    private static void Add(List<ContentIssue> issues, string path, string code, string message, string? jsonPath = null) =>
        issues.Add(new ContentIssue(path, code, message, jsonPath));
}
