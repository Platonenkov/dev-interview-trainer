using System.Collections.ObjectModel;

namespace Trainer.Domain.Levels;

/// <summary>Maps <see cref="Level"/> to its stable lowercase URL/content key and back.</summary>
public static class LevelKeys
{
    private static readonly Level[] AllLevels =
    [
        Level.Junior, Level.Middle, Level.Senior, Level.Lead, Level.Architect,
    ];

    // A wrapper keeps callers from casting the list back to an array and mutating shared state.
    private static readonly ReadOnlyCollection<Level> AllView = Array.AsReadOnly(AllLevels);

    /// <summary>All levels in ascending order. The list is read-only.</summary>
    public static IReadOnlyList<Level> All => AllView;

    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined level.</exception>
    public static string ToKey(Level level) => level switch
    {
        Level.Junior => "junior",
        Level.Middle => "middle",
        Level.Senior => "senior",
        Level.Lead => "lead",
        Level.Architect => "architect",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };

    /// <summary>Parses a key case-insensitively; returns false for null, empty or unknown input.</summary>
    public static bool TryParse(string? key, out Level level)
    {
        foreach (Level candidate in AllLevels)
        {
            if (string.Equals(key, ToKey(candidate), StringComparison.OrdinalIgnoreCase))
            {
                level = candidate;
                return true;
            }
        }

        level = default;
        return false;
    }
}
