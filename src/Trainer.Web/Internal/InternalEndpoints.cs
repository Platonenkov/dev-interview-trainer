using Microsoft.Extensions.Options;
using Trainer.Application.Content;
using Trainer.Web.Startup;

namespace Trainer.Web.Internal;

/// <param name="Status">The lowercase import outcome: <c>imported</c>, <c>unchanged</c> or <c>rejected</c>.</param>
public sealed record InternalImportDto(string Status, string Hash, int Imported, int Retired, IReadOnlyList<InternalImportIssueDto> Issues);

public sealed record InternalImportIssueDto(string Path, string Code, string Message);

/// <param name="ActiveContentHash">Hash of the active import, so a deploy script can tell whether an import call that failed on the wire had committed.</param>
public sealed record InternalVersionDto(string AppVersion, int ContentSchemaMin, int ContentSchemaMax, int RenderVersion, string? ActiveContentHash);

/// <summary>
/// The endpoints the deployment calls: <c>POST /internal/content/import</c> and <c>GET /internal/version</c>. They are
/// mapped only when <c>App:Internal:Port</c> is positive, and answered only on that socket port
/// (<see cref="InternalPortFilter"/>). Every other <c>/internal/*</c> request, of any method, gets an empty 404 on any port.
/// </summary>
public static class InternalEndpoints
{
    public const string ContentRootMissingCode = "content-root-missing";

    public static void Map(IEndpointRouteBuilder endpoints, bool mapEndpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder group = endpoints.MapGroup("/internal").AddEndpointFilter<InternalPortFilter>();
        if (mapEndpoints)
        {
            group.MapGet("/version", GetVersionAsync);
            group.MapPost("/content/import", ImportAsync).AddEndpointFilter<ImportTokenFilter>();
        }

        // Unknown paths and wrong methods under the prefix must not fall through to the HTML catch-all pages or a 405.
        group.Map("/{**path}", () => Results.NotFound());
    }

    private static async Task<IResult> GetVersionAsync(
        IContentSchemaInfo schema, IContentStatusQueries status, HttpContext context)
    {
        string? hash = await status.GetActiveHashAsync(context.RequestAborted);
        return Results.Json(
            new InternalVersionDto(AppVersion.Current, schema.MinSupported, schema.MaxSupported, schema.RenderVersion, hash),
            InternalJsonContext.Default.InternalVersionDto);
    }

    // The import runs under the host stopping token, not RequestAborted: a client that gives up must not cancel an import
    // half-way. A retry then waits on the importer's advisory lock and gets a definitive answer.
    private static async Task<IResult> ImportAsync(
        IContentImportService importer, IOptions<AppOptions> options, IHostEnvironment environment, IHostApplicationLifetime lifetime, HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(options.Value.ContentRoot))
        {
            return ContentRootMissing();
        }

        string root = Path.GetFullPath(options.Value.ContentRoot, environment.ContentRootPath);
        ContentImportResult result;
        try
        {
            result = await importer.ImportAsync(root, lifetime.ApplicationStopping);
        }
        catch (DirectoryNotFoundException)
        {
            return ContentRootMissing();
        }
        catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
        {
            // The host is shutting down mid-import; the caller may retry on the next process. The status is set directly because
            // a result type that resolves services can fail while the container is being disposed.
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Results.Empty;
        }

        InternalImportDto body = new(
            result.Status.ToString().ToLowerInvariant(),
            result.Hash,
            result.Imported,
            result.Retired,
            [.. result.Issues.Select(i => new InternalImportIssueDto(i.Path, i.Code, i.Message))]);
        int status = result.Status == ContentImportStatus.Rejected ? StatusCodes.Status422UnprocessableEntity : StatusCodes.Status200OK;
        return Results.Json(body, InternalJsonContext.Default.InternalImportDto, statusCode: status);
    }

    private static IResult ContentRootMissing() => Results.Json(
        new InternalImportDto(
            ContentImportStatus.Rejected.ToString().ToLowerInvariant(),
            string.Empty,
            0,
            0,
            [new InternalImportIssueDto(string.Empty, ContentRootMissingCode, "The configured content root does not exist.")]),
        InternalJsonContext.Default.InternalImportDto,
        statusCode: StatusCodes.Status422UnprocessableEntity);
}
