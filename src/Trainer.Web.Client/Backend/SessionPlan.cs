namespace Trainer.Web.Client.Backend;

/// <summary>
/// A session as a backend plans it: which questions, in which order, and where to get each one. The questions
/// themselves arrive lazily through <see cref="Questions"/>.
/// </summary>
/// <param name="PoolSize">How many questions matched the filters before the session size cut them.</param>
/// <param name="MyRatings">The user's own like or dislike per question id; null when the backend keeps none.</param>
/// <param name="Remainder">
/// Set when the plan holds only its opening question and the rest of the selection is still being computed (a static
/// <c>first=</c> link shows its question before the progress replay is done); null for a complete plan.
/// </param>
public sealed record SessionPlan(
    IReadOnlyList<string> QuestionIds,
    int PoolSize,
    IReadOnlyDictionary<string, string>? MyRatings,
    IQuestionSource Questions,
    Task<SessionRemainder>? Remainder = null)
{
    /// <summary>The same plan without <paramref name="excluded"/>, keeping the order of what is left.</summary>
    public SessionPlan Without(IReadOnlySet<string> excluded)
    {
        ArgumentNullException.ThrowIfNull(excluded);
        return this with { QuestionIds = [.. QuestionIds.Where(id => !excluded.Contains(id))] };
    }
}

/// <summary>The questions that follow a plan's opening question, and the size of the pool the whole session was drawn from.</summary>
public sealed record SessionRemainder(IReadOnlyList<string> QuestionIds, int PoolSize);
