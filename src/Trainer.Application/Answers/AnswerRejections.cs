namespace Trainer.Application.Answers;

/// <summary>Reason codes for answers that are acknowledged as rejected and never stored. They are part of the API.</summary>
public static class AnswerRejections
{
    /// <summary>No question has this id.</summary>
    public const string QuestionUnknown = "question-unknown";

    /// <summary>The question, its topic or its stack is retired.</summary>
    public const string QuestionRetired = "question-retired";

    /// <summary><c>answeredAt</c> is older than <see cref="AnswerTimePolicy.MaxAge"/>.</summary>
    public const string AnsweredTooOld = "answered-too-old";

    /// <summary><c>answeredAt</c> is ahead of the server by more than <see cref="AnswerTimePolicy.FutureTolerance"/>.</summary>
    public const string AnsweredInFuture = "answered-in-future";

    /// <summary>The entry could not be parsed; set by the endpoint parser, never by the recorder.</summary>
    public const string Invalid = "invalid";
}
