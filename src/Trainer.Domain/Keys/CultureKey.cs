using System.Collections.ObjectModel;

namespace Trainer.Domain.Keys;

/// <summary>Supported content and UI culture; the first URL segment. Only exact lowercase <c>ru</c> and <c>en</c> are accepted.</summary>
public readonly record struct CultureKey(string Value)
{
    private static readonly CultureKey[] SupportedCultures = [new("ru"), new("en")];

    // A wrapper keeps callers from casting the list back to an array and mutating shared state.
    private static readonly ReadOnlyCollection<CultureKey> SupportedView = Array.AsReadOnly(SupportedCultures);

    /// <summary>The supported cultures in declaration order. The list is read-only.</summary>
    public static IReadOnlyList<CultureKey> Supported => SupportedView;

    public static CultureKey Default => new("en");

    public static bool TryCreate(string? value, out CultureKey culture)
    {
        foreach (CultureKey candidate in SupportedCultures)
        {
            if (string.Equals(value, candidate.Value, StringComparison.Ordinal))
            {
                culture = candidate;
                return true;
            }
        }

        culture = default;
        return false;
    }

    public override string ToString() => Value;
}
