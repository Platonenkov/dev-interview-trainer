using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Trainer.Web.Islands;

/// <summary>The only render mode islands use (spec 8.2): WebAssembly, never Server.</summary>
public static class IslandRenderMode
{
    /// <summary>
    /// Without prerendering: the server renders a fixed-size skeleton and the island loads its data once, on the client,
    /// so nothing is fetched twice.
    /// </summary>
    public static readonly IComponentRenderMode NoPrerender = new InteractiveWebAssemblyRenderMode(prerender: false);
}
