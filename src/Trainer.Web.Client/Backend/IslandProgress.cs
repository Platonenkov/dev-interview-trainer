using Trainer.Contracts;

namespace Trainer.Web.Client.Backend;

/// <summary>What the sidebar and the session bar show about the user's progress.</summary>
/// <param name="DueNow">Questions waiting in the review queue.</param>
public sealed record IslandProgress(int StreakDays, int DueNow, IReadOnlyList<TopicProgressDto> Topics)
{
    /// <summary>Progress of a signed-in user; null for an anonymous visitor, who has none.</summary>
    public static IslandProgress? From(MeDto? me) => me is null ? null : new IslandProgress(me.StreakDays, me.DueNow, me.Topics);
}
