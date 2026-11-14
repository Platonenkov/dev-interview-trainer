namespace Trainer.Domain.Grading;

/// <summary>
/// Pure grading rules for the question types that have a machine-checkable answer. Runs in the WASM client
/// (the server trusts the grade) and in tests; a used hint caps every result at <see cref="Grade.Partial"/>.
/// </summary>
public static class AutoGrading
{
    public static Grade CapForHint(Grade grade, bool usedHint) =>
        usedHint && grade > Grade.Partial ? Grade.Partial : grade;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name",
        Justification = "Mirrors the QuestionType.Single content key; the name is part of the shared grading API.")]
    public static Grade Single(string correctOptionId, string pickedOptionId, bool usedHint)
    {
        ArgumentNullException.ThrowIfNull(correctOptionId);
        ArgumentNullException.ThrowIfNull(pickedOptionId);

        Grade grade = string.Equals(correctOptionId, pickedOptionId, StringComparison.Ordinal) ? Grade.Knew : Grade.DidntKnow;
        return CapForHint(grade, usedHint);
    }

    /// <summary>
    /// Exact set is <see cref="Grade.Knew"/>; at least one correct pick and at most one mistake (an extra or a
    /// missed option) is <see cref="Grade.Partial"/>; anything else is <see cref="Grade.DidntKnow"/>. An empty correct
    /// set is invalid content (the content validator rejects it); with an empty pick it would grade
    /// <see cref="Grade.Knew"/>, the exact-set rule applied to two empty sets.
    /// </summary>
    public static Grade Multi(IReadOnlyCollection<string> correctOptionIds, IReadOnlyCollection<string> pickedOptionIds, bool usedHint)
    {
        ArgumentNullException.ThrowIfNull(correctOptionIds);
        ArgumentNullException.ThrowIfNull(pickedOptionIds);

        HashSet<string> correct = new(correctOptionIds, StringComparer.Ordinal);
        HashSet<string> picked = new(pickedOptionIds, StringComparer.Ordinal);

        int hits = picked.Count(correct.Contains);
        int mistakes = (picked.Count - hits) + (correct.Count - hits);

        Grade grade = mistakes == 0 ? Grade.Knew
            : hits >= 1 && mistakes <= 1 ? Grade.Partial
            : Grade.DidntKnow;
        return CapForHint(grade, usedHint);
    }

    public static Grade TypedOutput(IReadOnlyList<string> acceptedOutputs, string typed, bool usedHint)
    {
        ArgumentNullException.ThrowIfNull(acceptedOutputs);
        ArgumentNullException.ThrowIfNull(typed);

        string normalisedTyped = OutputText.Normalise(typed);
        bool matches = acceptedOutputs.Any(a => string.Equals(OutputText.Normalise(a), normalisedTyped, StringComparison.Ordinal));
        return CapForHint(matches ? Grade.Knew : Grade.DidntKnow, usedHint);
    }

    /// <summary>
    /// The selection must equal one of the accepted line sets exactly; a superset or subset is wrong. An empty accepted
    /// set is invalid content (the content validator rejects it); an empty selection would match it.
    /// </summary>
    public static Grade FindBug(IReadOnlyList<IReadOnlyList<int>> acceptedLineSets, IReadOnlyCollection<int> selectedLines, bool usedHint)
    {
        ArgumentNullException.ThrowIfNull(acceptedLineSets);
        ArgumentNullException.ThrowIfNull(selectedLines);

        HashSet<int> selected = new(selectedLines);
        bool matches = acceptedLineSets.Any(set => selected.SetEquals(set));
        return CapForHint(matches ? Grade.Knew : Grade.DidntKnow, usedHint);
    }
}
