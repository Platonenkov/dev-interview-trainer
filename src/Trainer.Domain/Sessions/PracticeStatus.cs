namespace Trainer.Domain.Sessions;

/// <summary>Which of the stack's questions Practice offers, relative to the user's review states.</summary>
public enum PracticeStatus
{
    All,

    /// <summary>Never answered: no review state for a signed-in user; for an anonymous one the client lists answered questions in <c>Exclude</c>.</summary>
    New,

    /// <summary>Review state due now. Needs a signed-in user; an anonymous query matches nothing.</summary>
    Due,
}
