using System.Reflection;

namespace Trainer.Web.Internal;

public static class AppVersion
{
    /// <summary>The Web assembly informational version (the SDK appends the commit hash), or <c>dev</c> when the build carries none.</summary>
    public static string Current { get; } =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { Length: > 0 } version
            ? version
            : "dev";
}
