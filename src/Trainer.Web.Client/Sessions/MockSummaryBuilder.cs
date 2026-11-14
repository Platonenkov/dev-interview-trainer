using Trainer.Contracts;
using Trainer.Domain.Grading;

namespace Trainer.Web.Client.Sessions;

/// <summary>
/// The summary of a mock by topic, in the order topics first appear (spec 5.3). Every question counts exactly once, so
/// the four counts over all topics add up to the question count, as <c>PUT /api/mock-sessions/{id}</c> requires.
/// </summary>
public sealed class MockSummaryBuilder
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "A service like AnswerFactory, so the island and the session share one way of summarising.")]
    public MockSummaryDto Build(MockSession session, DateTimeOffset finishedAt)
    {
        ArgumentNullException.ThrowIfNull(session);
        List<MockTopicResultDto> topics = [];
        foreach (IGrouping<string, QuestionAttempt> topic in session.Attempts.GroupBy(a => a.Question.Topic, StringComparer.Ordinal))
        {
            topics.Add(new MockTopicResultDto(
                topic.Key,
                topic.Count(a => a.Grade == Grade.Knew),
                topic.Count(a => a.Grade == Grade.Partial),
                topic.Count(a => a.Grade == Grade.DidntKnow),
                topic.Count(a => a.Grade is null)));
        }

        MockContext context = session.Context;
        return new MockSummaryDto(
            context.Stack, context.Level, session.Attempts.Count, context.Timed, context.StartedAt, finishedAt, topics);
    }
}
