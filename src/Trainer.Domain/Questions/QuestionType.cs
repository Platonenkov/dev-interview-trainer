namespace Trainer.Domain.Questions;

/// <summary>Question format. The numeric values are persisted in the database and must never change; new members get new numbers.</summary>
public enum QuestionType
{
    Open = 1,
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifiers should not contain type names", Justification = "The key single is a fixed content vocabulary value.")]
    Single = 2,
    Multi = 3,
    Output = 4,
    FindBug = 5,
    Situation = 6,
}
