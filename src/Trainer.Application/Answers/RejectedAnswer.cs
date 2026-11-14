namespace Trainer.Application.Answers;

/// <param name="ClientAnswerId">The id as the client sent it.</param>
/// <param name="Reason">One of <see cref="AnswerRejections"/>.</param>
public sealed record RejectedAnswer(string ClientAnswerId, string Reason);
