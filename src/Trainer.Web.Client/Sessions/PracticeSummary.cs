using Trainer.Contracts;
using Trainer.Domain.Grading;

namespace Trainer.Web.Client.Sessions;

/// <summary>One topic of a finished session: how the answers went and which questions they were.</summary>
public sealed record PracticeTopicSummary(
    string Topic,
    string Title,
    int Knew,
    int Partial,
    int DidntKnow,
    IReadOnlyList<(QuestionDto Question, Grade Grade)> Questions);

public static class PracticeSummary
{
    /// <summary>Groups graded results by topic, topics and questions in the order they were first shown.</summary>
    public static IReadOnlyList<PracticeTopicSummary> ByTopic(IReadOnlyList<(QuestionDto Question, Grade Grade)> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return
        [
            .. results
                .GroupBy(r => r.Question.Topic, StringComparer.Ordinal)
                .Select(g => new PracticeTopicSummary(
                    g.Key,
                    g.First().Question.TopicTitle,
                    g.Count(r => r.Grade == Grade.Knew),
                    g.Count(r => r.Grade == Grade.Partial),
                    g.Count(r => r.Grade == Grade.DidntKnow),
                    [.. g])),
        ];
    }
}
