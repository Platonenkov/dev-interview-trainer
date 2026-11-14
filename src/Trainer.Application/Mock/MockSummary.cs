using Trainer.Domain.Keys;
using Trainer.Domain.Levels;

namespace Trainer.Application.Mock;

/// <summary>The summary a client sends when a mock interview ends (spec 8.8, <c>PUT /api/mock-sessions/{id}</c>).</summary>
public sealed record MockSummary(
    StackKey Stack,
    Level Level,
    int QuestionCount,
    bool Timed,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    IReadOnlyList<MockTopicResult> Topics);
