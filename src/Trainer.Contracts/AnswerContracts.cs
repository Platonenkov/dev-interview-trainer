namespace Trainer.Contracts;

/// <summary>
/// One answer as the client reports it. <paramref name="AnsweredAt"/> is nullable on the wire so that a missing value
/// is rejected as invalid instead of binding to <c>0001-01-01</c>; the client always sets it (ADR 0005).
/// </summary>
/// <param name="Grade"><c>knew</c>, <c>partial</c> or <c>didnt-know</c>.</param>
/// <param name="Mode"><c>practice</c>, <c>mock</c> or <c>review</c>.</param>
public sealed record AnswerDto(
    string ClientAnswerId,
    string QuestionId,
    string Grade,
    bool UsedHint,
    string Mode,
    string? MockSessionId,
    DateTimeOffset? AnsweredAt);

/// <summary>The request body of <c>POST /api/answers/batch</c> and the outbox document (spec 8.8).</summary>
public sealed record AnswerBatch(int Version, IReadOnlyList<AnswerDto> Answers)
{
    public const int CurrentVersion = 1;
}

/// <summary>Both lists hold client answer ids; acknowledged answers (accepted or rejected) leave the outbox.</summary>
public sealed record AnswerBatchResponse(IReadOnlyList<string> Accepted, IReadOnlyList<RejectedAnswerDto> Rejected);

public sealed record RejectedAnswerDto(string ClientAnswerId, string Reason);
