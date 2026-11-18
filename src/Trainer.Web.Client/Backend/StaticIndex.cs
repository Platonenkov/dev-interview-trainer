using Trainer.Contracts;
using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;
using Trainer.Domain.Sessions;

namespace Trainer.Web.Client.Backend;

/// <summary>
/// The only place index entries (plain strings from JSON) become domain types. An entry whose id, level, type or topic the
/// domain does not know is skipped everywhere, so a malformed or newer-format entry can neither be selected nor counted.
/// </summary>
public static class StaticIndex
{
    /// <summary>Ids of the questions the index lists; progress of any other id is kept but not counted.</summary>
    public static IReadOnlySet<QuestionId> LiveIds(StaticIndexDto index)
    {
        ArgumentNullException.ThrowIfNull(index);
        return new HashSet<QuestionId>(Candidates(index).Select(c => c.Id));
    }

    /// <summary>The live questions in index order, with the attributes the Practice filters look at.</summary>
    public static IReadOnlyList<PracticeCandidate> Candidates(StaticIndexDto index)
    {
        ArgumentNullException.ThrowIfNull(index);
        List<PracticeCandidate> candidates = new(index.Questions.Count);
        foreach (StaticIndexEntryDto entry in index.Questions)
        {
            if (QuestionId.TryCreate(entry.Id, out QuestionId id)
                && LevelKeys.TryParse(entry.Level, out Level level)
                && QuestionTypeKeys.TryParse(entry.Type, out QuestionType type)
                && TopicKey.TryCreate(entry.Topic, out TopicKey topic))
            {
                candidates.Add(new PracticeCandidate(id, level, topic, type));
            }
        }

        return candidates;
    }

    /// <summary>The entry for <paramref name="id"/>, or null when the index does not list it.</summary>
    public static StaticIndexEntryDto? Find(StaticIndexDto index, string id)
    {
        ArgumentNullException.ThrowIfNull(index);
        foreach (StaticIndexEntryDto entry in index.Questions)
        {
            if (string.Equals(entry.Id, id, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }
}
