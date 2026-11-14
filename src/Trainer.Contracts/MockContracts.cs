namespace Trainer.Contracts;

/// <summary>The summary a client sends with <c>PUT /api/mock-sessions/{id}</c> when a mock interview ends.</summary>
public sealed record MockSummaryDto(
    string Stack,
    string Level,
    int QuestionCount,
    bool Timed,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    IReadOnlyList<MockTopicResultDto> Topics);

public sealed record MockTopicResultDto(string Topic, int Knew, int Partial, int DidntKnow, int Unanswered);
