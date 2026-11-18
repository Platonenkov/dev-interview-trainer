namespace Trainer.Content.Validation;

/// <summary>Codes of the semantic rules enforced by <see cref="IContentValidator"/>; the strings are stable and appear in CLI output and CI annotations.</summary>
public static class IssueCodes
{
    public const string SchemaVersionUnsupported = "schema-version-unsupported";
    public const string IdInvalid = "id-invalid";
    public const string IdDuplicate = "id-duplicate";
    public const string SlugInvalid = "slug-invalid";
    public const string SlugDuplicate = "slug-duplicate";
    public const string PathMismatch = "path-mismatch";
    public const string StackUnknown = "stack-unknown";
    public const string TopicUnknown = "topic-unknown";
    public const string TaxonomyDuplicate = "taxonomy-duplicate";
    public const string StackKeyReserved = "stack-key-reserved";
    public const string TaxonomyLocaleIncomplete = "taxonomy-locale-incomplete";
    public const string LocaleUnsupported = "locale-unsupported";
    public const string LevelInvalid = "level-invalid";
    public const string TypeInvalid = "type-invalid";
    public const string GradingInvalid = "grading-invalid";
    public const string GradingTypeMismatch = "grading-type-mismatch";
    public const string OptionsRequired = "options-required";
    public const string OptionsForbidden = "options-forbidden";
    public const string OptionsCorrectCount = "options-correct-count";
    public const string OptionIdDuplicate = "option-id-duplicate";
    public const string OutputExpectRequired = "output-expect-required";
    public const string OutputAnswerMode = "output-answer-mode";
    public const string AcceptedOutputsMismatch = "accepted-outputs-mismatch";
    public const string ExpectKindInvalid = "expect-kind-invalid";
    public const string ExpectValueRequired = "expect-value-required";
    public const string CodeRequired = "code-required";
    public const string HarnessInvalid = "harness-invalid";
    public const string HarnessNoneForbidden = "harness-none-forbidden";
    public const string HarnessNoneReason = "harness-none-reason";
    public const string BuglinesOutOfRange = "buglines-out-of-range";
    public const string BugLinesRequired = "buglines-required";
    public const string FixedExpectRequired = "fixed-expect-required";
    public const string LocaleIncomplete = "locale-incomplete";
    public const string LinksRequired = "links-required";
    public const string LinkKindInvalid = "link-kind-invalid";
    public const string LinkLangInvalid = "link-lang-invalid";
    public const string LinkScheme = "link-scheme";
    public const string MarkdownLinkScheme = "markdown-link-scheme";
    public const string TagInvalid = "tag-invalid";
    public const string RedirectTargetUnknown = "redirect-target-unknown";
    public const string RedirectCycle = "redirect-cycle";
    public const string RedirectFromLive = "redirect-from-live";
    public const string RedirectSelf = "redirect-self";
    public const string RedirectDuplicate = "redirect-duplicate";
    public const string VerifiedInFuture = "verified-in-future";
}
