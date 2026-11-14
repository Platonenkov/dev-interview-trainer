namespace Trainer.Domain.Grading;

/// <summary>Grades answers to questions without a machine-checkable key (open, situation).</summary>
public interface IAnswerGrader
{
    ValueTask<Grade> GradeAsync(SelfGradedAnswer answer, CancellationToken ct);
}
