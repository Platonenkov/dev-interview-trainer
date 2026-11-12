using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Diagnostics;
using Trainer.Domain.Keys;

namespace Trainer.Web.Routing;

/// <summary>
/// The only request culture source: the <c>Culture</c> route value. On the re-executed not-found page the route is
/// <c>/not-found</c>, so the culture of the original path is used instead and a Russian visitor gets a Russian 404.
/// </summary>
public sealed class RouteCultureProvider : RequestCultureProvider
{
    public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        string? routeValue = httpContext.Request.RouteValues[CultureAndStackMiddleware.CultureRouteKey] as string;
        if (CultureKey.TryCreate(routeValue, out CultureKey culture))
        {
            return Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(culture.Value));
        }

        IStatusCodeReExecuteFeature? reExecute = httpContext.Features.Get<IStatusCodeReExecuteFeature>();
        CultureKey? original = CulturePath.FromPath(reExecute?.OriginalPath);
        return Task.FromResult<ProviderCultureResult?>(original is { } key ? new ProviderCultureResult(key.Value) : null);
    }
}
