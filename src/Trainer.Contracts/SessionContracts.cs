namespace Trainer.Contracts;

/// <summary>Practice session query. Empty filter lists mean "no restriction"; <paramref name="Status"/> is <c>all</c>, <c>new</c> or <c>due</c>.</summary>
/// <param name="FirstQuestionId">A question that opens the session when it exists in the stack and culture, whatever the filters say.</param>
/// <param name="ExcludeQuestionIds">
/// Ids left out in every status (at most 1000): the questions an anonymous client answered (for <c>new</c>) and the
/// questions just seen (for "New session"). <paramref name="FirstQuestionId"/> is never left out.
/// </param>
public sealed record PracticeSessionRequest(
    string Culture,
    string Stack,
    IReadOnlyList<string> Levels,
    IReadOnlyList<string> Topics,
    IReadOnlyList<string> Types,
    string Status,
    int Count,
    string? FirstQuestionId,
    IReadOnlyList<string> ExcludeQuestionIds);

/// <summary>Mock interview query: questions of <paramref name="Level"/> and the level below it.</summary>
public sealed record MockSessionRequest(string Culture, string Stack, string Level, IReadOnlyList<string> Topics, int Count);

/// <summary>Review query: the signed-in user's due questions, most overdue first.</summary>
public sealed record ReviewSessionRequest(string Culture, string Stack, int Count);

/// <param name="PoolSize">How many questions matched before the count limit was applied.</param>
/// <param name="MyRatings">
/// The signed-in user's rating keys by question id, for rated questions only; null for an anonymous session and for
/// mock sessions, which show no feedback controls.
/// </param>
public sealed record SessionDto(IReadOnlyList<QuestionDto> Questions, int PoolSize, IReadOnlyDictionary<string, string>? MyRatings = null);

/// <param name="PoolSize">Questions available at the level and the level below, before composing the session.</param>
/// <param name="RepeatsLikely">True when the pool is smaller than about three times the requested count (spec: warn before the session starts).</param>
/// <param name="SuggestedCount">
/// A count that keeps repeats unlikely: a third of the pool, within the mock range 5..50, or the whole pool when it holds
/// fewer than 5 questions. It is 0 for an empty pool. A client offers "Use N" only when this is below the count it asked for.
/// </param>
public sealed record MockSessionDto(IReadOnlyList<QuestionDto> Questions, int PoolSize, bool RepeatsLikely, int SuggestedCount);
