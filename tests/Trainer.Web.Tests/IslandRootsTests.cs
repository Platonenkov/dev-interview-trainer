using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Trainer.TestSupport;
using Trainer.Web.Client.Islands;

namespace Trainer.Web.Tests;

/// <summary>
/// The trimmed Release client keeps an island component only through <c>IslandRoots</c>: the server names islands only in
/// its render markers. A missing entry leaves the island dead in a published build alone, so the set is checked here,
/// without a publish.
/// </summary>
public sealed partial class IslandRootsTests
{
    [GeneratedRegex(@"<(?<name>[A-Z]\w*)\s[^>]*@rendermode=", RegexOptions.CultureInvariant)]
    private static partial Regex RenderModeUse();

    private static readonly Assembly Client = typeof(PracticeIsland).Assembly;

    private static string[] Rooted()
    {
        MethodInfo keep = Client.GetType("Trainer.Web.Client.Islands.IslandRoots", throwOnError: true)!
            .GetMethod("Keep", BindingFlags.Public | BindingFlags.Static)!;
        return [.. keep.GetCustomAttributes<DynamicDependencyAttribute>()
            .Select(a =>
            {
                Assert.Equal(DynamicallyAccessedMemberTypes.All, a.MemberTypes);
                return a.Type!.FullName!;
            })
            .Order(StringComparer.Ordinal)];
    }

    [Fact]
    public void IslandRoots_KeepsEveryIslandComponent()
    {
        string[] islands = [.. Client.GetTypes()
            .Where(t => t.Namespace == "Trainer.Web.Client.Islands" && typeof(IComponent).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => t.FullName!)
            .Order(StringComparer.Ordinal)];

        Assert.NotEmpty(islands);
        Assert.Equal(islands, Rooted());
    }

    [Fact]
    public void IslandRoots_KeepsEveryComponentTheServerRendersWithARenderMode()
    {
        string web = Path.Combine(ContentWorkspace.RepositoryRoot(), "src", "Trainer.Web");
        string[] used = [.. Directory.EnumerateFiles(web, "*.razor", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => RenderModeUse().Matches(File.ReadAllText(f)).Select(m => m.Groups["name"].Value))
            .Distinct(StringComparer.Ordinal)
            .Select(name => Client.GetTypes().Single(t => t.Name == name && typeof(IComponent).IsAssignableFrom(t)).FullName!)
            .Order(StringComparer.Ordinal)];

        Assert.Equal(4, used.Length);
        Assert.Equal(used, Rooted());
    }
}
