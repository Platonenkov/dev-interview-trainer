using Microsoft.AspNetCore.Antiforgery;
using Trainer.Contracts;

namespace Trainer.Web.Api;

/// <summary>
/// Validates the <c>X-CSRF-TOKEN</c> header against the antiforgery cookie and the current user explicitly (spec 8.8):
/// JSON endpoints carry no antiforgery metadata, so the antiforgery middleware would not check them.
/// </summary>
public sealed class AntiforgeryValidationFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            return ApiEndpoints.Error(StatusCodes.Status400BadRequest, ApiErrorCodes.Antiforgery);
        }

        return await next(context);
    }
}
