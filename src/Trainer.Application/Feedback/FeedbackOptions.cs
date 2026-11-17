namespace Trainer.Application.Feedback;

/// <summary>Question feedback configuration, bound once at startup from <c>App:Feedback</c>; a change needs a restart.</summary>
public sealed class FeedbackOptions
{
    public const string SectionName = "App:Feedback";

    /// <summary>
    /// New reports one user may create within a rolling 24 hours. Editing an open report does not count. Must be
    /// positive, or startup fails.
    /// </summary>
    public int ReportsPerDay { get; init; } = 10;
}
