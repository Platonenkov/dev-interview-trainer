using Trainer.Domain.Keys;

namespace Trainer.Application.Mock;

/// <summary>Per-topic counts of one mock interview.</summary>
public sealed record MockTopicResult(TopicKey Topic, int Knew, int Partial, int DidntKnow, int Unanswered);
