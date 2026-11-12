using Trainer.Web.ContentState;
using Trainer.Domain.Keys;

namespace Trainer.Web.Routing;

/// <summary>
/// Answers 404 for an unsupported culture or an unknown stack, judged by the <c>Culture</c> and <c>Stack</c> route values
/// of the matched endpoint. It reads only <see cref="ContentSnapshotProvider"/>, so a cache hit never reaches the database.
/// The empty 404 is turned into the not-found page by the status-code-pages middleware.
/// </summary>
public sealed class CultureAndStackMiddleware(RequestDelegate next, ContentSnapshotProvider snapshots)
{
    public const string CultureRouteKey = "Culture";
    public const string StackRouteKey = "Stack";

    public Task InvokeAsync(HttpContext context)
    {
        RouteValueDictionary values = context.Request.RouteValues;

        if (values.TryGetValue(CultureRouteKey, out object? culture) && !CultureKey.TryCreate(culture as string, out _))
        {
            return NotFound(context);
        }

        if (values.TryGetValue(StackRouteKey, out object? stack) && !snapshots.Current.HasStack(stack as string))
        {
            return NotFound(context);
        }

        return next(context);
    }

    private static Task NotFound(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }
}
