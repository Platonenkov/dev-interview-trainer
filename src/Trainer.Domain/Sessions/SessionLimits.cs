namespace Trainer.Domain.Sessions;

/// <summary>Bounds of the session queries; the API validates requests against them and the query services enforce them.</summary>
public static class SessionLimits
{
    public const int DefaultCount = 20;
    public const int MaxCount = 50;
    public const int MinPracticeCount = 1;
    public const int MinMockCount = 5;
    public const int MinReviewCount = 1;
    public const int MaxExcludedQuestions = 1000;
}
