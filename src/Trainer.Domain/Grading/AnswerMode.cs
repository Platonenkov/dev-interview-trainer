namespace Trainer.Domain.Grading;

/// <summary>How an answer was given. The numbers are stored in the database; do not renumber.</summary>
public enum AnswerMode
{
    Practice = 1,
    Mock = 2,
    Review = 3,
}
