namespace Trainer.Domain.Grading;

/// <summary>Result of one answer. The numbers are stored in the database; do not renumber.</summary>
public enum Grade
{
    DidntKnow = 0,
    Partial = 1,
    Knew = 2,
}
