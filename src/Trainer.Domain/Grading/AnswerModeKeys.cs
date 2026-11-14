using System.Collections.ObjectModel;

namespace Trainer.Domain.Grading;

/// <summary>Maps <see cref="AnswerMode"/> to its wire key and back. Keys travel only between our own client and server.</summary>
public static class AnswerModeKeys
{
    private static readonly AnswerMode[] AllModes = [AnswerMode.Practice, AnswerMode.Mock, AnswerMode.Review];

    private static readonly ReadOnlyCollection<AnswerMode> AllView = Array.AsReadOnly(AllModes);

    /// <summary>All modes in declaration order. The list is read-only.</summary>
    public static IReadOnlyList<AnswerMode> All => AllView;

    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined mode.</exception>
    public static string ToKey(AnswerMode mode) => mode switch
    {
        AnswerMode.Practice => "practice",
        AnswerMode.Mock => "mock",
        AnswerMode.Review => "review",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    /// <summary>Parses a key case-sensitively; returns false for null, empty or unknown input.</summary>
    public static bool TryParse(string? key, out AnswerMode mode)
    {
        foreach (AnswerMode candidate in AllModes)
        {
            if (string.Equals(key, ToKey(candidate), StringComparison.Ordinal))
            {
                mode = candidate;
                return true;
            }
        }

        mode = default;
        return false;
    }
}
