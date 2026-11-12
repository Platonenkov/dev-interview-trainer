using System.Collections.ObjectModel;

namespace Trainer.Domain.Questions;

/// <summary>Maps <see cref="GradingMode"/> to its content key. The key <c>llm</c> is rejected until LLM grading exists.</summary>
public static class GradingModeKeys
{
    private static readonly GradingMode[] AllModes = [GradingMode.Self, GradingMode.Auto];

    // A wrapper keeps callers from casting the list back to an array and mutating shared state.
    private static readonly ReadOnlyCollection<GradingMode> AllView = Array.AsReadOnly(AllModes);

    /// <summary>The grading modes that content may declare. The list is read-only.</summary>
    public static IReadOnlyList<GradingMode> All => AllView;

    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined mode.</exception>
    public static string ToKey(GradingMode mode) => mode switch
    {
        GradingMode.Self => "self",
        GradingMode.Auto => "auto",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    /// <summary>Parses a key case-insensitively; returns false for null, empty or unknown input.</summary>
    public static bool TryParse(string? key, out GradingMode mode)
    {
        foreach (GradingMode candidate in AllModes)
        {
            if (string.Equals(key, ToKey(candidate), StringComparison.OrdinalIgnoreCase))
            {
                mode = candidate;
                return true;
            }
        }

        mode = default;
        return false;
    }
}
