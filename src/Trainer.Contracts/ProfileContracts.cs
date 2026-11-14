namespace Trainer.Contracts;

public sealed record ProfileDto(
    int StreakDays,
    int TotalAnswered,
    int DueNow,
    DateTimeOffset? NextDueAt,
    IReadOnlyList<TopicProgressDto> Topics,
    IReadOnlyList<LevelProgressDto> Levels,
    IReadOnlyList<MockHistoryDto> RecentMocks);

/// <param name="LiveQuestions">Questions currently in the catalog for the topic.</param>
public sealed record TopicProgressDto(string Topic, string Title, int LiveQuestions, int Answered, int Known, int Due);

public sealed record LevelProgressDto(string Level, int LiveQuestions, int Answered, int Known);

public sealed record MockHistoryDto(
    string Id,
    string Level,
    int QuestionCount,
    DateTimeOffset FinishedAt,
    int Knew,
    int Partial,
    int DidntKnow,
    int Unanswered);
