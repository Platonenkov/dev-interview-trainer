using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Trainer.Web.Client.Interop;

/// <summary>
/// Counts runtime starts in <c>window.__ditStarts</c> (islands-start.js) for the tests and the measurements. A
/// <c>[JSImport]</c>, not an <c>IJSRuntime</c> call: it runs right before the island reports ready, and the first
/// <c>IJSRuntime</c> call of a runtime builds reflection-based JSON metadata (the Task 21 start-up profile).
/// </summary>
internal static partial class RuntimeStarts
{
    public static void Count()
    {
        // Rendered outside a browser (component tests) there is nothing to count.
        if (OperatingSystem.IsBrowser())
        {
            CountStart();
        }
    }

    [SupportedOSPlatform("browser")]
    [JSImport("globalThis.ditCountRuntimeStart")]
    private static partial void CountStart();
}
