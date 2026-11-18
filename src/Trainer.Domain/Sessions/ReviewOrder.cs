using Trainer.Domain.Questions;

namespace Trainer.Domain.Sessions;

/// <summary>The Review rules: the user's due questions that are still live, most overdue first.</summary>
public static class ReviewOrder
{
    /// <param name="states">The user's review states as question id and due time.</param>
    /// <param name="live">Ids of the live questions of the stack and culture; states of other questions do not count.</param>
    /// <returns>
    /// At most <paramref name="count"/> ids ordered by due time, ties by id (ordinal), and the number of due live states.
    /// </returns>
    public static (IReadOnlyList<QuestionId> Ids, int PoolSize) Pick(
        IEnumerable<(QuestionId Id, DateTimeOffset DueAt)> states,
        IReadOnlySet<QuestionId> live,
        DateTimeOffset now,
        int count)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(live);
        if (count < SessionLimits.MinReviewCount || count > SessionLimits.MaxCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count), count, $"The count must be between {SessionLimits.MinReviewCount} and {SessionLimits.MaxCount}.");
        }

        List<(QuestionId Id, DateTimeOffset DueAt)> due = [.. states.Where(s => s.DueAt <= now && live.Contains(s.Id))];
        // Ordinal order equals the database's ORDER BY id only because question ids are uppercase ULIDs.
        due.Sort((a, b) =>
        {
            int byDue = a.DueAt.CompareTo(b.DueAt);
            return byDue != 0 ? byDue : string.CompareOrdinal(a.Id.Value, b.Id.Value);
        });
        return ([.. due.Take(count).Select(s => s.Id)], due.Count);
    }
}
