namespace Trainer.Domain.Feedback;

/// <summary>Why a user reported a question. The numbers are stored in the database; do not renumber.</summary>
public enum ReportReason
{
    WrongAnswer = 1,
    UnclearWording = 2,
    Outdated = 3,
    BrokenLink = 4,
    Other = 5,
}
