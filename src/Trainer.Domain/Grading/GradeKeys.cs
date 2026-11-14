using System.Collections.ObjectModel;

namespace Trainer.Domain.Grading;

/// <summary>Maps <see cref="Grade"/> to its wire key and back. Keys travel only between our own client and server.</summary>
public static class GradeKeys
{
    private static readonly Grade[] AllGrades = [Grade.DidntKnow, Grade.Partial, Grade.Knew];

    // A wrapper keeps callers from casting the list back to an array and mutating shared state.
    private static readonly ReadOnlyCollection<Grade> AllView = Array.AsReadOnly(AllGrades);

    /// <summary>All grades from worst to best. The list is read-only.</summary>
    public static IReadOnlyList<Grade> All => AllView;

    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined grade.</exception>
    public static string ToKey(Grade grade) => grade switch
    {
        Grade.DidntKnow => "didnt-know",
        Grade.Partial => "partial",
        Grade.Knew => "knew",
        _ => throw new ArgumentOutOfRangeException(nameof(grade), grade, null),
    };

    /// <summary>Parses a key case-sensitively; returns false for null, empty or unknown input.</summary>
    public static bool TryParse(string? key, out Grade grade)
    {
        foreach (Grade candidate in AllGrades)
        {
            if (string.Equals(key, ToKey(candidate), StringComparison.Ordinal))
            {
                grade = candidate;
                return true;
            }
        }

        grade = default;
        return false;
    }
}
