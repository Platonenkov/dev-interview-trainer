using Trainer.Domain.Questions;

namespace Trainer.Domain.Grading;

/// <param name="QuestionId">The answered question.</param>
/// <param name="Type">The question type; only open and situation are self-graded.</param>
/// <param name="Text">Free-form answer text. Reserved for a future LLM grader; the self-assessment grader ignores it.</param>
/// <param name="SelfAssessment">The grade the learner gave themselves.</param>
/// <param name="UsedHint">Whether a hint was opened before answering.</param>
public sealed record SelfGradedAnswer(QuestionId QuestionId, QuestionType Type, string? Text, Grade SelfAssessment, bool UsedHint);
