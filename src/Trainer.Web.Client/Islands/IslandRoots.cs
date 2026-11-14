using System.Diagnostics.CodeAnalysis;

namespace Trainer.Web.Client.Islands;

/// <summary>
/// Keeps the island components in the trimmed build. The server names them only in its render markers, so nothing in
/// this assembly references them and <c>TrimMode=full</c> removed them: the published runtime failed with "Root component
/// type ... could not be found". Each is kept with every member the renderer reaches by reflection; what they use is
/// then kept by ordinary references, and nothing else is.
/// </summary>
internal static class IslandRoots
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(PracticeIsland))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(MockIsland))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ReviewIsland))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ProfileIsland))]
    public static void Keep()
    {
    }
}
