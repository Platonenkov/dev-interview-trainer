using Trainer.Domain.Questions;

namespace Trainer.Domain.Grading;

/// <summary>Trusts the learner's own grade, capped by hint use. Handles only open and situation questions.</summary>
public sealed class SelfAssessmentGrader : IAnswerGrader
{
    /// <exception cref="ArgumentException">The question type is graded automatically, not by self-assessment.</exception>
    public ValueTask<Grade> GradeAsync(SelfGradedAnswer answer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(answer);

        if (answer.Type is not (QuestionType.Open or QuestionType.Situation))
        {
            throw new ArgumentException($"Question type {answer.Type} is graded automatically.", nameof(answer));
        }

        return ValueTask.FromResult(AutoGrading.CapForHint(answer.SelfAssessment, answer.UsedHint));
    }
}
