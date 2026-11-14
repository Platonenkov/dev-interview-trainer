namespace Trainer.Web.Islands;

/// <summary>
/// Marks a page that hosts a WebAssembly island. The attribute lands in the page's endpoint metadata, and only such
/// pages get <c>blazor.web.js</c>, the island stylesheet and a CSP that allows WebAssembly compilation; every other
/// page stays free of the runtime (spec 8.2, ADR 0009). Island pages must also be noindex and never output-cached.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class InteractiveIslandPageAttribute : Attribute
{
    /// <summary>True when the matched endpoint of <paramref name="context"/> is an island page.</summary>
    public static bool IsMatched(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.GetEndpoint()?.Metadata.GetMetadata<InteractiveIslandPageAttribute>() is not null;
    }
}
