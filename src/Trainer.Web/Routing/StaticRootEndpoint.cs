using Microsoft.AspNetCore.Http.HttpResults;
using Trainer.Web.Components.Pages;
using Trainer.Web.Startup;

namespace Trainer.Web.Routing;

/// <summary>
/// Maps <c>GET /</c> and the language endpoint for an edition. The server edition redirects by cookie and
/// <c>Accept-Language</c>; GitHub Pages cannot do either, so the static edition serves a chooser page whose script picks
/// the language in the browser, and has no <c>/lang</c> endpoint at all.
/// </summary>
public static class StaticRootEndpoint
{
    public static void Map(IEndpointRouteBuilder endpoints, SiteEdition edition)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (edition == SiteEdition.Static)
        {
            endpoints.MapGet("/", () => new RazorComponentResult<LanguageChooser>());
            return;
        }

        RootRedirectEndpoint.Map(endpoints);
        LanguageEndpoint.Map(endpoints);
    }
}
