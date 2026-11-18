using Microsoft.AspNetCore.Diagnostics;
using Trainer.Contracts;
using Trainer.Domain.Keys;
using Trainer.Web.ContentState;

namespace Trainer.Web.StaticSite;

/// <summary>
/// The content shards of the static edition (ADR 0023), mapped in that edition only:
/// <c>GET /data/{lang}/{stackKey}/index.{hash}.json</c> and <c>GET /data/{lang}/q/{id}.{hash}.json</c>. The export crawls
/// them into files, so a name carries the hash of its bytes and any other name, including a stale hash, is an empty 404
/// with <c>Content-Length: 0</c>. The route values are not called <c>culture</c> or <c>stack</c> on purpose: the culture
/// middleware judges those names and would turn a miss into the HTML not-found page.
/// </summary>
public static class StaticContentEndpoints
{
    public const string Prefix = "/data";
    public const string JsonContentType = "application/json; charset=utf-8";

    private static readonly string[] ReadMethods = [HttpMethods.Get, HttpMethods.Head];

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder group = endpoints.MapGroup(Prefix).AddEndpointFilter(DisableStatusPagesAsync);
        group.MapMethods("/{lang}/q/{id}.{hash}.json", ReadMethods, GetQuestionAsync);
        group.MapMethods("/{lang}/{stackKey}/index.{hash}.json", ReadMethods, GetIndexAsync);
        group.Map("/{**path}", (HttpContext context) => NotFound(context));
    }

    // The status-code pages middleware would replace the empty 404 with the HTML page.
    private static ValueTask<object?> DisableStatusPagesAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (context.HttpContext.Features.Get<IStatusCodePagesFeature>() is { } statusPages)
        {
            statusPages.Enabled = false;
        }

        return next(context);
    }

    private static async Task<IResult> GetIndexAsync(
        string lang, string stackKey, string hash, HttpContext context, ContentSnapshotProvider snapshots, StaticContentCatalog catalog)
    {
        if (!CultureKey.TryCreate(lang, out CultureKey culture)
            || !StackKey.TryCreate(stackKey, out StackKey stack)
            || !snapshots.Current.HasStack(stackKey)
            || !StaticContentHash.IsValid(hash))
        {
            return NotFound(context);
        }

        StaticContentFiles files = await catalog.GetAsync(culture, stack, context.RequestAborted);
        return string.Equals(files.IndexPath, StaticContentPaths.Index(lang, stackKey, hash), StringComparison.Ordinal)
            ? Results.Bytes(files.Index, JsonContentType)
            : NotFound(context);
    }

    private static async Task<IResult> GetQuestionAsync(
        string lang, string id, string hash, HttpContext context, ContentSnapshotProvider snapshots, StaticContentCatalog catalog)
    {
        if (!CultureKey.TryCreate(lang, out CultureKey culture) || !StaticContentHash.IsValid(hash))
        {
            return NotFound(context);
        }

        // The route names no stack, so look in each; ids are unique across stacks.
        string path = StaticContentPaths.Question(lang, id, hash);
        foreach (string stack in snapshots.Current.Stacks.Order(StringComparer.Ordinal))
        {
            StaticContentFiles files = await catalog.GetAsync(culture, new StackKey(stack), context.RequestAborted);
            if (files.Files.TryGetValue(path, out ReadOnlyMemory<byte> bytes))
            {
                return Results.Bytes(bytes, JsonContentType);
            }
        }

        return NotFound(context);
    }

    private static IResult NotFound(HttpContext context)
    {
        context.Response.ContentLength = 0;
        return Results.NotFound();
    }
}
