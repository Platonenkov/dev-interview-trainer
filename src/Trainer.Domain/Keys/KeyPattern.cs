using System.Text.RegularExpressions;

namespace Trainer.Domain.Keys;

/// <summary>Shared validation for kebab-case keys used in URLs and content paths.</summary>
internal static partial class KeyPattern
{
    internal const int MaxLength = 80;

    // The end anchor is \z because a plain dollar would also accept a trailing newline.
    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Kebab();

    internal static bool IsValid(string? value) =>
        value is { Length: > 0 and <= MaxLength } && Kebab().IsMatch(value);
}
