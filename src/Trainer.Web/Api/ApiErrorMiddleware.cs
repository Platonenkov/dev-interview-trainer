using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using Trainer.Contracts;

namespace Trainer.Web.Api;

/// <summary>
/// The single writer of <c>/api</c> error bodies. It wraps everything from authentication to the endpoint (and unmatched
/// routes), so a script never gets the HTML not-found page, a redirect, or an exception page: every non-success response
/// it can still write is an <see cref="ApiErrorDto"/>. Every <c>/api</c> response is <c>no-store</c>.
/// </summary>
/// <remarks>
/// Map it for <c>/api</c> only, after the status-code-pages middleware (whose re-execution it switches off) and before
/// routing. Its start-of-response callback is registered before the cookie handler's, so it runs after it and its
/// <c>no-store</c> wins over the handler's headers on a reissued cookie.
/// </remarks>
public sealed partial class ApiErrorMiddleware(RequestDelegate next, ILogger<ApiErrorMiddleware> logger)
{
    /// <summary>Larger request bodies are refused with 413; a full outbox batch of 500 answers is well under it.</summary>
    public const long MaxRequestBodyBytes = 256 * 1024;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Features.Get<IStatusCodePagesFeature>() is { } statusCodePages)
        {
            statusCodePages.Enabled = false;
        }

        context.Response.OnStarting(static state =>
        {
            ((HttpContext)state).Response.Headers[HeaderNames.CacheControl] = "no-store";
            return Task.CompletedTask;
        }, context);

        // TestServer ignores the body size feature, and a declared length can be refused before anything is read.
        if (context.Request.ContentLength > MaxRequestBodyBytes)
        {
            await WriteAsync(context, StatusCodes.Status413PayloadTooLarge);
            return;
        }

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
        {
            bodySize.MaxRequestBodySize = MaxRequestBodyBytes;
        }

        try
        {
            await next(context);
        }
        catch (BadHttpRequestException ex) when (!context.Response.HasStarted)
        {
            // Development throws for malformed JSON, binding failures and wrong content types; elsewhere they arrive as
            // bodiless statuses and are handled below.
            context.Response.Clear();
            await WriteAsync(context, ex.StatusCode);
            return;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            LogUnhandled(logger, ex, context.Request.Method, context.Request.Path);
            context.Response.Clear();
            await WriteAsync(context, StatusCodes.Status500InternalServerError);
            return;
        }

        if (!context.Response.HasStarted && context.Response.StatusCode >= StatusCodes.Status400BadRequest)
        {
            await WriteAsync(context, context.Response.StatusCode);
        }
    }

    /// <summary>
    /// The <see cref="ApiErrorCodes"/> value a bodiless status is reported with. A 409 is deliberately not mapped to
    /// <see cref="ApiErrorCodes.OwnerMismatch"/>: only the owner filters produce it, with their own body, and the client
    /// stops flushing on that code (ADR 0008), so a bodiless 409 from anywhere else must not be read as an account change.
    /// </summary>
    public static string CodeFor(int status) => status switch
    {
        StatusCodes.Status401Unauthorized => ApiErrorCodes.SignInRequired,
        StatusCodes.Status403Forbidden => ApiErrorCodes.Forbidden,
        StatusCodes.Status404NotFound => ApiErrorCodes.NotFound,
        StatusCodes.Status405MethodNotAllowed => ApiErrorCodes.MethodNotAllowed,
        StatusCodes.Status413PayloadTooLarge => ApiErrorCodes.PayloadTooLarge,
        StatusCodes.Status415UnsupportedMediaType => ApiErrorCodes.UnsupportedMediaType,
        StatusCodes.Status429TooManyRequests => ApiErrorCodes.RateLimited,
        >= StatusCodes.Status500InternalServerError => ApiErrorCodes.ServerError,
        _ => ApiErrorCodes.Validation,
    };

    private static Task WriteAsync(HttpContext context, int status)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(
            new ApiErrorDto(CodeFor(status), null), TrainerJsonContext.Default.ApiErrorDto, contentType: null, context.RequestAborted);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Unhandled exception in {Method} {Path}; answered 500.")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string method, PathString path);
}
