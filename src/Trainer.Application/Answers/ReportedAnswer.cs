using Trainer.Domain.Grading;
using Trainer.Domain.Ids;
using Trainer.Domain.Questions;

namespace Trainer.Application.Answers;

/// <summary>One answer from the client outbox, already parsed (spec 8.8).</summary>
/// <param name="AnsweredAt">Stamped by the client with its calibrated server clock when the answer was given (ADR 0005).</param>
public sealed record ReportedAnswer(
    ClientAnswerId ClientAnswerId,
    QuestionId QuestionId,
    Grade Grade,
    bool UsedHint,
    AnswerMode Mode,
    MockSessionId? MockSessionId,
    DateTimeOffset AnsweredAt);
