using Trainer.Contracts;
using Trainer.Domain.Grading;
using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;
using Trainer.Domain.Scheduling;
using Trainer.Domain.Sessions;

namespace Trainer.Web.Client.Backend;

/// <summary>
/// Builds the profile from the index and the replayed states with the meaning of the server's <c>ProgressQueries</c>:
/// live questions come from the index, a question is answered when it has a state, known when its last grade was
/// "knew", due when its due time has come. States of questions the index does not list are not counted.
/// <c>LocalProfileParityTests</c> keeps the two in step.
/// </summary>
public static class LocalProfileBuilder
{
    /// <summary>The server profile lists the five newest mocks.</summary>
    private const int RecentMockCount = 5;

    /// <param name="mocks">The finished mocks of this browser, in any order.</param>
    /// <param name="utcOffset">The device's offset, which decides where a streak day ends.</param>
    /// <param name="topicOrder">
    /// The topics in the order the server rendered them into the island (by title in the page's culture). The client has no
    /// culture data to sort titles itself, and the index lists topics by key on purpose, so rows follow this order; topics it
    /// does not list come last, by key.
    /// </param>
    public static ProfileDto Build(
        StaticIndexDto index,
        LocalProgress progress,
        IReadOnlyList<MockHistoryDto> mocks,
        DateTimeOffset now,
        TimeSpan utcOffset,
        IReadOnlyList<TopicDto>? topicOrder = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(mocks);

        Dictionary<string, string> titles = [];
        foreach (TopicDto topic in index.Topics)
        {
            titles.TryAdd(topic.Key, topic.Title);
        }

        // Per (topic, level) cell, so the topic rows and the level rows add up from the same counts.
        Dictionary<(TopicKey Topic, Level Level), Cell> cells = [];
        DateTimeOffset? nextDue = null;
        foreach (PracticeCandidate candidate in StaticIndex.Candidates(index))
        {
            if (!cells.TryGetValue((candidate.Topic, candidate.Level), out Cell? cell))
            {
                cell = new Cell();
            }

            cells[(candidate.Topic, candidate.Level)] = cell;
            cell.Live++;
            if (!progress.States.TryGetValue(candidate.Id, out ReviewState? state))
            {
                continue;
            }

            cell.Answered++;
            cell.Known += state.LastGrade == Grade.Knew ? 1 : 0;
            if (state.DueAt <= now)
            {
                cell.Due++;
            }
            else if (nextDue is null || state.DueAt < nextDue)
            {
                nextDue = state.DueAt;
            }
        }

        Dictionary<string, int> position = [];
        foreach (TopicDto topic in topicOrder ?? [])
        {
            position.TryAdd(topic.Key, position.Count);
        }

        TopicProgressDto[] topics = [.. cells
            .GroupBy(c => c.Key.Topic.Value, c => c.Value, StringComparer.Ordinal)
            .Select(g => new TopicProgressDto(
                g.Key,
                titles.GetValueOrDefault(g.Key, g.Key),
                g.Sum(c => c.Live),
                g.Sum(c => c.Answered),
                g.Sum(c => c.Known),
                g.Sum(c => c.Due)))
            .OrderBy(t => position.GetValueOrDefault(t.Topic, int.MaxValue))
            .ThenBy(t => t.Topic, StringComparer.Ordinal)];

        LevelProgressDto[] levels = [.. LevelKeys.All.Select(level =>
        {
            Cell[] ofLevel = [.. cells.Where(c => c.Key.Level == level).Select(c => c.Value)];
            return new LevelProgressDto(LevelKeys.ToKey(level), ofLevel.Sum(c => c.Live), ofLevel.Sum(c => c.Answered), ofLevel.Sum(c => c.Known));
        })];

        MockHistoryDto[] recent = [.. mocks
            .OrderByDescending(m => m.FinishedAt)
            .ThenByDescending(m => m.Id, StringComparer.Ordinal)
            .Take(RecentMockCount)];

        // TotalAnswered counts answered live questions, as the server's does, not log entries.
        return new ProfileDto(
            progress.StreakDays(now, utcOffset),
            topics.Sum(t => t.Answered),
            topics.Sum(t => t.Due),
            nextDue,
            topics,
            levels,
            recent);
    }

    private sealed class Cell
    {
        public int Live { get; set; }

        public int Answered { get; set; }

        public int Known { get; set; }

        public int Due { get; set; }
    }
}
