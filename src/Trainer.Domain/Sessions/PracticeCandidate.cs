using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;

namespace Trainer.Domain.Sessions;

/// <summary>A live question of the stack and culture with the attributes the Practice filters look at.</summary>
public sealed record PracticeCandidate(QuestionId Id, Level Level, TopicKey Topic, QuestionType Type);
