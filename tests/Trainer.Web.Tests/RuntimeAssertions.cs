using System.Text.RegularExpressions;

namespace Trainer.Web.Tests;

/// <summary>Public pages never load the WebAssembly runtime (spec 8.2, ADR 0009).</summary>
internal static partial class RuntimeAssertions
{
    [GeneratedRegex(@"\sdata-preload-runtime=""[^""]*""", RegexOptions.CultureInvariant)]
    private static partial Regex PreloadAttribute();

    /// <summary>
    /// Fails when the page references the runtime anywhere but in the <c>data-preload-runtime</c> attributes of its
    /// entry-point links, which only name the files <c>runtime-preload.js</c> may prefetch on hover.
    /// </summary>
    public static void AssertNoRuntime(string body)
    {
        string rest = PreloadAttribute().Replace(body, "");
        Assert.DoesNotContain("_framework/", rest, StringComparison.Ordinal);
        Assert.DoesNotContain("blazor.web", rest, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet.js", rest, StringComparison.Ordinal);
    }
}
