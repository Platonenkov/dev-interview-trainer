namespace Trainer.Content;

/// <summary>
/// The closed vocabularies and key patterns of the content format. The JSON schema generator and the semantic
/// validator both read them from here so that editors and the build cannot disagree about what is allowed.
/// </summary>
public static class ContentVocabulary
{
    /// <summary>Mirrors the Trainer.Domain key validation; ECMA-262 dollar matches only at the end of the input.</summary>
    public const string KebabPattern = "^[a-z0-9]+(-[a-z0-9]+)*$";

    /// <summary>Mirrors <c>QuestionId</c>: an uppercase Crockford base32 ULID.</summary>
    public const string UlidPattern = "^[0-7][0-9A-HJKMNP-TV-Z]{25}$";

    public const int KeyMaxLength = 80;

    public static IReadOnlyList<string> Harnesses { get; } = ["statements", "program", "none"];

    public static IReadOnlyList<string> ExpectKinds { get; } = ["output", "exception", "compile-error", "nondeterministic"];

    public static IReadOnlyList<string> LinkKinds { get; } = ["docs", "spec", "article", "video", "book"];

    public const string HarnessNone = "none";

    public const string ExpectKindOutput = "output";

    public const string ExpectKindException = "exception";
}
