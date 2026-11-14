namespace Trainer.Application.Answers;

/// <summary>How the server treats a client-reported time (ADR 0005).</summary>
public enum TimeVerdict
{
    /// <summary>Not in the future and not older than <see cref="AnswerTimePolicy.MaxAge"/>; stored as reported.</summary>
    Accept,

    /// <summary>Ahead of the server by at most <see cref="AnswerTimePolicy.FutureTolerance"/>; stored as the server's now.</summary>
    Clamp,

    /// <summary>Older than <see cref="AnswerTimePolicy.MaxAge"/>; rejected.</summary>
    TooOld,

    /// <summary>Ahead of the server by more than <see cref="AnswerTimePolicy.FutureTolerance"/>; rejected.</summary>
    InFuture,
}
