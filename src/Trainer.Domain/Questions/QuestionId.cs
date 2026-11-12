using System.Text.RegularExpressions;

namespace Trainer.Domain.Questions;

/// <summary>
/// Stable question identifier: an uppercase Crockford base32 ULID. The first character is limited to 0-7
/// because a ULID is 128 bits, so the 26-character encoding cannot exceed 7 there.
/// </summary>
public readonly partial record struct QuestionId(string Value)
{
    // The end anchor is \z because a plain dollar would also accept a trailing newline.
    [GeneratedRegex(@"^[0-7][0-9A-HJKMNP-TV-Z]{25}\z", RegexOptions.CultureInvariant)]
    private static partial Regex UlidPattern();

    public static bool TryCreate(string? value, out QuestionId id)
    {
        if (value is not null && UlidPattern().IsMatch(value))
        {
            id = new QuestionId(value);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value;
}
