using System.Globalization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Net.Http.Headers;
using Trainer.Application.Questions;
using Trainer.Domain.Keys;
using Trainer.Web.Seo;

namespace Trainer.Web.OpenGraph;

/// <summary>
/// <c>GET</c> and <c>HEAD</c> on <c>/og/{lang}/{stackKey}/{slug}/{fingerprint}.png</c> and
/// <c>/og/{lang}/site/{fingerprint}.png</c>. The route values are not called <c>culture</c> or <c>stack</c> on purpose:
/// the culture middleware judges those names and would turn a miss into the HTML not-found page. Every miss, and every
/// other verb, under <c>/og</c> is an empty 404, because a crawler wants a status, not a page. The Cache-Control and
/// Set-Cookie rules of these responses live in <see cref="OgResponseHeadersMiddleware"/>.
/// </summary>
public static class OgImageEndpoints
{
    public const string PngContentType = "image/png";
    public const string ImmutableCacheControl = "public, max-age=31536000, immutable";
    public const string RedirectCacheControl = "public, max-age=3600";
    public const string BusyCacheControl = "no-store";

    private const int RetryAfterSeconds = 5;

    private static readonly string[] ImageMethods = [HttpMethods.Get, HttpMethods.Head];

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder group = endpoints.MapGroup(OgImageLinks.Prefix).AddEndpointFilter(DisableStatusPagesAsync);
        group.MapMethods("/{lang}/" + OgImageLinks.SiteSegment + "/{fingerprint}.png", ImageMethods, GetSiteAsync);
        group.MapMethods("/{lang}/{stackKey}/{slug}/{fingerprint}.png", ImageMethods, GetQuestionAsync);
        group.Map("/{**path}", () => Results.NotFound());
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

    private static async Task<IResult> GetSiteAsync(
        string lang, string fingerprint, HttpContext context, OgImageLinks links, SitePaths paths, OgCardRenderer renderer, OgImageCache cache)
    {
        if (!CultureKey.TryCreate(lang, out CultureKey culture))
        {
            return Results.NotFound();
        }

        OgCard card = links.SiteCard(culture, context.Request);
        string current = OgFingerprint.Of(card);
        if (!string.Equals(fingerprint, current, StringComparison.Ordinal))
        {
            return Results.Redirect(paths.Asset(OgImageLinks.SitePath(culture, current)), permanent: true);
        }

        return await ServeAsync(context, cache, OgImageCache.SiteFileName(culture.Value, current), renderer, card);
    }

    private static async Task<IResult> GetQuestionAsync(
        string lang, string stackKey, string slug, string fingerprint,
        HttpContext context, IQuestionQueries questions, OgImageLinks links, SitePaths paths, OgCardRenderer renderer, OgImageCache cache)
    {
        if (!CultureKey.TryCreate(lang, out CultureKey culture)
            || !StackKey.TryCreate(stackKey, out StackKey stack)
            || !Slug.TryCreate(slug, out Slug questionSlug))
        {
            return Results.NotFound();
        }

        // A retired question keeps its page but is noindex, so it advertises the site card and has no card of its own.
        QuestionPageModel? question = await questions.GetAsync(culture, stack, questionSlug, context.RequestAborted);
        if (question is null || question.IsRetired)
        {
            return Results.NotFound();
        }

        OgCard card = links.QuestionCard(culture, question, context.Request);
        string current = OgFingerprint.Of(card);
        if (!string.Equals(fingerprint, current, StringComparison.Ordinal))
        {
            return Results.Redirect(paths.Asset(OgImageLinks.QuestionPath(culture, stack.Value, questionSlug.Value, current)), permanent: true);
        }

        string fileName = OgImageCache.QuestionFileName(culture.Value, stack.Value, questionSlug.Value, current);
        return await ServeAsync(context, cache, fileName, renderer, card);
    }

    private static async Task<IResult> ServeAsync(HttpContext context, OgImageCache cache, string fileName, OgCardRenderer renderer, OgCard card)
    {
        try
        {
            byte[] png = await cache.GetOrRenderAsync(fileName, () => renderer.RenderPng(card), context.RequestAborted);
            return Results.Bytes(png, PngContentType);
        }
        catch (OgRenderBusyException)
        {
            context.Response.Headers[HeaderNames.RetryAfter] = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }
}
