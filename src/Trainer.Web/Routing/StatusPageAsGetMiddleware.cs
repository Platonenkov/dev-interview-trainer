using Microsoft.AspNetCore.Diagnostics;

namespace Trainer.Web.Routing;

/// <summary>
/// Renders a re-executed status page as a <c>GET</c>. The status-code-pages middleware re-executes with the original
/// method, so a <c>POST</c> to an unknown path would reach the not-found component as a form post without an antiforgery
/// token and come back 400 instead of 404.
/// </summary>
/// <remarks>Place it right after <c>UseStatusCodePagesWithReExecute</c>; it acts only on the re-executed request.</remarks>
public sealed class StatusPageAsGetMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string original = context.Request.Method;
        if (context.Features.Get<IStatusCodeReExecuteFeature>() is null
            || HttpMethods.IsGet(original)
            || HttpMethods.IsHead(original))
        {
            await next(context);
            return;
        }

        context.Request.Method = HttpMethods.Get;
        try
        {
            await next(context);
        }
        finally
        {
            // The status-code-pages middleware restores the path and query but not the method; the request log line and the
            // request metric read it after this middleware returns and must see the real method.
            context.Request.Method = original;
        }
    }
}
