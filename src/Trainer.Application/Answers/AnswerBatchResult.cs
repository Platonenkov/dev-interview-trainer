namespace Trainer.Application.Answers;

/// <summary>
/// Acknowledgement of a batch: every distinct client id appears exactly once, either accepted (stored now or
/// earlier) or rejected, in batch order. The client removes both kinds from its outbox.
/// </summary>
public sealed record AnswerBatchResult(IReadOnlyList<string> Accepted, IReadOnlyList<RejectedAnswer> Rejected);
