using Trainer.Domain.Questions;

namespace Trainer.Domain.Sessions;

/// <summary>
/// The Practice selection rules, shared by the server (which runs the same rules in SQL, kept in step by a parity test)
/// and by a client that holds the whole question list. Not thread-safe unless the injected <see cref="Random"/> is (use
/// <see cref="Random.Shared"/> for a singleton).
/// </summary>
public sealed class PracticeSelector(Random random)
{
    /// <param name="live">Every live question of the stack and culture.</param>
    /// <param name="dueAtById">
    /// The user's review states by question. Null means no states are known (an anonymous user): <c>New</c> then matches
    /// every question that is not excluded and <c>Due</c> matches nothing.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">The count is outside the practice range.</exception>
    /// <exception cref="ArgumentException">More questions are excluded than <see cref="SessionLimits.MaxExcludedQuestions"/> allows.</exception>
    public PracticePick Select(
        IReadOnlyList<PracticeCandidate> live,
        PracticeSelection selection,
        IReadOnlyDictionary<QuestionId, DateTimeOffset>? dueAtById,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Count < SessionLimits.MinPracticeCount || selection.Count > SessionLimits.MaxCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(selection), selection.Count, $"The count must be between {SessionLimits.MinPracticeCount} and {SessionLimits.MaxCount}.");
        }

        if (selection.Exclude.Count > SessionLimits.MaxExcludedQuestions)
        {
            throw new ArgumentException($"At most {SessionLimits.MaxExcludedQuestions} excluded questions are allowed.", nameof(selection));
        }

        HashSet<QuestionId> excluded = [.. selection.Exclude];
        List<QuestionId> rest = [];
        bool firstIsLive = false;
        foreach (PracticeCandidate candidate in live)
        {
            // The first question is outside every filter, so it is taken out of the shuffle before they apply.
            if (selection.First is { } first && candidate.Id == first)
            {
                firstIsLive = true;
            }
            else if (Matches(candidate, selection, excluded, dueAtById, now))
            {
                rest.Add(candidate.Id);
            }
        }

        int poolSize = rest.Count + (firstIsLive ? 1 : 0);
        Shuffle(rest);

        List<QuestionId> ids = new(Math.Min(selection.Count, poolSize));
        if (firstIsLive)
        {
            ids.Add(selection.First!.Value);
        }

        ids.AddRange(rest.Take(selection.Count - ids.Count));
        return new PracticePick(ids, poolSize);
    }

    private static bool Matches(
        PracticeCandidate candidate,
        PracticeSelection selection,
        HashSet<QuestionId> excluded,
        IReadOnlyDictionary<QuestionId, DateTimeOffset>? dueAtById,
        DateTimeOffset now)
    {
        if ((selection.Levels.Count > 0 && !selection.Levels.Contains(candidate.Level))
            || (selection.Topics.Count > 0 && !selection.Topics.Contains(candidate.Topic))
            || (selection.Types.Count > 0 && !selection.Types.Contains(candidate.Type))
            || excluded.Contains(candidate.Id))
        {
            return false;
        }

        return selection.Status switch
        {
            PracticeStatus.All => true,
            PracticeStatus.New => dueAtById is null || !dueAtById.ContainsKey(candidate.Id),
            PracticeStatus.Due => dueAtById is not null && dueAtById.TryGetValue(candidate.Id, out DateTimeOffset dueAt) && dueAt <= now,
            _ => throw new ArgumentOutOfRangeException(nameof(selection), selection.Status, null),
        };
    }

    private void Shuffle(List<QuestionId> items)
    {
        for (int i = items.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}
