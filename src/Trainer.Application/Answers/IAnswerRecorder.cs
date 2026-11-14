using Trainer.Domain.Users;

namespace Trainer.Application.Answers;

/// <summary>Stores reported answers idempotently and schedules reviews from them (spec 8.8, ADR 0005).</summary>
public interface IAnswerRecorder
{
    /// <summary>Spec 8.8: "batch size ≤ 500".</summary>
    const int MaxBatchSize = 500;

    /// <summary>
    /// Real UTC offsets run from −12:00 to +14:00, so ±14 hours covers them all. Every check of a client offset uses
    /// this constant.
    /// </summary>
    const int MaxUtcOffsetMinutes = 840;

    /// <summary>
    /// Records a batch. A client id that is already stored is accepted without any other effect, whoever owns it and
    /// whatever the payload (the first write wins). New answers are checked against <see cref="AnswerTimePolicy"/>
    /// and the question (unknown or retired), stored, and applied to the review schedule in
    /// (<c>answeredAt</c>, <c>clientAnswerId</c>) order. Duplicate ids inside the batch count once (the first wins).
    /// </summary>
    /// <param name="clientUtcOffsetMinutes">
    /// The device offset; stored on the user (for streak days) when it is within ±<see cref="MaxUtcOffsetMinutes"/>,
    /// ignored otherwise.
    /// </param>
    /// <exception cref="ArgumentException">The batch has more than <see cref="MaxBatchSize"/> answers.</exception>
    Task<AnswerBatchResult> RecordAsync(
        UserId userId, IReadOnlyList<ReportedAnswer> answers, short? clientUtcOffsetMinutes, CancellationToken ct);
}
