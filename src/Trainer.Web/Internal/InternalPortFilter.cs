using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace Trainer.Web.Internal;

/// <summary>
/// Answers an empty 404 unless the request arrived on the internal socket port. The check is on the connection, not on
/// the <c>Host</c> header, which a client controls: through nginx on the public port a <c>Host: x:8081</c> must not reach
/// these endpoints. The status-code pages feature is switched off for every request that gets here, otherwise the HTML
/// not-found page would replace the empty body of a 404 or 401 and tell a scanner that the route exists.
/// </summary>
public sealed class InternalPortFilter(IOptions<InternalOptions> options) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        HttpContext http = context.HttpContext;
        if (http.Features.Get<IStatusCodePagesFeature>() is { } statusPages)
        {
            statusPages.Enabled = false;
        }

        int port = options.Value.Port;
        return port > 0 && http.Connection.LocalPort == port
            ? next(context)
            : ValueTask.FromResult<object?>(Results.NotFound());
    }
}
