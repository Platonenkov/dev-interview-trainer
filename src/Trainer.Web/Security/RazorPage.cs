using Microsoft.AspNetCore.Components.Endpoints;

namespace Trainer.Web.Security;

/// <summary>Tells a Razor component page, the only response kind these middlewares act on, from everything else.</summary>
internal static class RazorPage
{
    /// <summary>
    /// True when routing matched a component page, which carries <see cref="ComponentTypeMetadata"/>. Static assets,
    /// the health checks and the minimal endpoints do not, and nothing is matched at all for an unknown path.
    /// </summary>
    public static bool IsMatched(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<ComponentTypeMetadata>() is not null;

    public static bool IsAuthenticated(HttpContext context) => context.User.Identity?.IsAuthenticated == true;
}
