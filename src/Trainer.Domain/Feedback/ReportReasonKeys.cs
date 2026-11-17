using System.Collections.ObjectModel;

namespace Trainer.Domain.Feedback;

/// <summary>Maps <see cref="ReportReason"/> to its wire key and back. Keys travel only between our own client and server.</summary>
public static class ReportReasonKeys
{
    private static readonly ReportReason[] AllReasons =
        [ReportReason.WrongAnswer, ReportReason.UnclearWording, ReportReason.Outdated, ReportReason.BrokenLink, ReportReason.Other];

    // A wrapper keeps callers from casting the list back to an array and mutating shared state.
    private static readonly ReadOnlyCollection<ReportReason> AllView = Array.AsReadOnly(AllReasons);

    /// <summary>All reasons in the order a form lists them. The list is read-only.</summary>
    public static IReadOnlyList<ReportReason> All => AllView;

    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined reason.</exception>
    public static string ToKey(ReportReason reason) => reason switch
    {
        ReportReason.WrongAnswer => "wrong-answer",
        ReportReason.UnclearWording => "unclear-wording",
        ReportReason.Outdated => "outdated",
        ReportReason.BrokenLink => "broken-link",
        ReportReason.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    /// <summary>Parses a key case-sensitively; returns false for null, empty or unknown input.</summary>
    public static bool TryParse(string? key, out ReportReason reason)
    {
        foreach (ReportReason candidate in AllReasons)
        {
            if (string.Equals(key, ToKey(candidate), StringComparison.Ordinal))
            {
                reason = candidate;
                return true;
            }
        }

        reason = default;
        return false;
    }
}
