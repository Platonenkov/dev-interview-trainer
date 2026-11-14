namespace Trainer.Contracts.Storage;

/// <summary>
/// The progress of the active mock interview, kept in <c>localStorage</c> under <c>dit.mock.v1.{owner}.active</c> so a
/// reload resumes it. The questions live once in a separate key; this document stays small because it is rewritten on
/// every step. <paramref name="Revision"/> grows by one with every save, and a save only succeeds over the previous
/// revision, so a second tab writing the same mock is detected instead of silently overwritten.
/// </summary>
/// <param name="Owner">The Telegram user id as text, or <c>anon</c>.</param>
/// <param name="Phase">The mock phase key (answering, assessment, finished), owned by the Mock island.</param>
/// <param name="Index">The zero-based position of the current question.</param>
/// <param name="FinishedAt">
/// When the answering ended (the user finished or the time ran out), server clock; null while answering. Optional on the
/// wire: documents written before it existed read as null.
/// </param>
public sealed record MockProgress(
    int Version,
    string SessionId,
    string Owner,
    string Stack,
    string Culture,
    string Level,
    int QuestionCount,
    bool Timed,
    DateTimeOffset StartedAt,
    DateTimeOffset? Deadline,
    string Phase,
    int Index,
    long Revision,
    IReadOnlyList<AttemptState> Attempts,
    MockCompletionState? Completion,
    DateTimeOffset? FinishedAt = null)
{
    public const int CurrentVersion = 1;
}

/// <summary>Everything needed to restore one question attempt of a mock exactly as the user left it.</summary>
/// <param name="OptionOrder">Option ids in the shuffled order the user saw.</param>
/// <param name="Lines">1-based code lines marked on a find-bug question.</param>
/// <param name="Frozen">True once the time ran out or the attempt was submitted: the response can no longer change.</param>
/// <param name="Grade">The final grade key once graded: the automatic grade, or the grader's result for a self-assessed question.</param>
/// <param name="SelfGrade">The grade key the user chose during self-assessment, before the grader's hint cap.</param>
/// <param name="AnsweredAt">When an automatically graded question was graded (server clock).</param>
/// <param name="AssessedAt">When a self-assessed question was graded (server clock).</param>
/// <param name="ReferenceRevealed">
/// The reference answer of a self-assessed question was shown. Optional on the wire: documents written before it existed
/// read as false.
/// </param>
public sealed record AttemptState(
    string ClientAnswerId,
    IReadOnlyList<string> OptionOrder,
    IReadOnlyList<string> Picked,
    IReadOnlyList<int> Lines,
    string Typed,
    string Notes,
    bool UsedHint,
    DateTimeOffset? LastInputAt,
    bool Frozen,
    string? Grade,
    DateTimeOffset? AnsweredAt,
    string? SelfGrade,
    DateTimeOffset? AssessedAt,
    bool Skipped,
    bool ReferenceRevealed = false);

/// <summary>
/// A finished mock whose results are still being handed over: the answers to the outbox, the summary to the pending
/// summary queue. The flags record which hand-over is done, so a reload repeats only the missing one.
/// </summary>
public sealed record MockCompletionState(
    DateTimeOffset FinishedAt,
    IReadOnlyList<AnswerDto> Answers,
    MockSummaryDto Summary,
    bool AnswersQueued,
    bool SummaryQueued);
