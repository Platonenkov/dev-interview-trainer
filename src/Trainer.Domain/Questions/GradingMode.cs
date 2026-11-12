namespace Trainer.Domain.Questions;

/// <summary>How an answer is graded. LLM grading is intentionally absent in v1. The numeric values are persisted and must never change.</summary>
public enum GradingMode
{
    Self = 1,
    Auto = 2,
}
