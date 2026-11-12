using System.Collections.ObjectModel;

namespace Trainer.Domain.Questions;

/// <summary>Maps <see cref="QuestionType"/> to its content key (<c>find-bug</c> for <see cref="QuestionType.FindBug"/>) and back.</summary>
public static class QuestionTypeKeys
{
    private static readonly QuestionType[] AllTypes =
    [
        QuestionType.Open, QuestionType.Single, QuestionType.Multi,
        QuestionType.Output, QuestionType.FindBug, QuestionType.Situation,
    ];

    // A wrapper keeps callers from casting the list back to an array and mutating shared state.
    private static readonly ReadOnlyCollection<QuestionType> AllView = Array.AsReadOnly(AllTypes);

    /// <summary>All question types in declaration order. The list is read-only.</summary>
    public static IReadOnlyList<QuestionType> All => AllView;

    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined type.</exception>
    public static string ToKey(QuestionType type) => type switch
    {
        QuestionType.Open => "open",
        QuestionType.Single => "single",
        QuestionType.Multi => "multi",
        QuestionType.Output => "output",
        QuestionType.FindBug => "find-bug",
        QuestionType.Situation => "situation",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>Parses a key case-insensitively; returns false for null, empty or unknown input.</summary>
    public static bool TryParse(string? key, out QuestionType type)
    {
        foreach (QuestionType candidate in AllTypes)
        {
            if (string.Equals(key, ToKey(candidate), StringComparison.OrdinalIgnoreCase))
            {
                type = candidate;
                return true;
            }
        }

        type = default;
        return false;
    }
}
