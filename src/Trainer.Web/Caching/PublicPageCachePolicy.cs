using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Trainer.Application.Catalog;
using Trainer.Domain.Keys;
using Trainer.Web.Catalog;
using Trainer.Web.ContentState;
using Trainer.Web.Routing;

namespace Trainer.Web.Caching;

/// <summary>
/// Output cache policy for anonymous public pages. It is a custom policy rather than a tweak of the default one so
/// that the raw query string is never part of the key: only the content generation and, for the catalog, the
/// canonical form of the parsed query are, so every spelling of one selection shares one entry.
/// </summary>
public sealed class PublicPageCachePolicy(bool varyByCatalogQuery) : IOutputCachePolicy
{
    public const string PolicyName = "public";

    /// <summary>The same policy for the catalog page, whose key additionally carries the canonical query.</summary>
    public const string CatalogPolicyName = "public-catalog";

    public const string GenerationKey = "gen";
    public const string QueryKey = "q";

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        HttpContext http = context.HttpContext;
        bool cacheable = IsCacheableRequest(http.Request);

        context.EnableOutputCaching = cacheable;
        context.AllowCacheLookup = cacheable;
        context.AllowCacheStorage = cacheable;
        context.AllowLocking = cacheable;
        if (!cacheable)
        {
            return ValueTask.CompletedTask;
        }

        // Read now, not when the response is written: a render that overlaps an import then lands under the old
        // generation's key and can never be served as the new content.
        ContentSnapshot snapshot = http.RequestServices.GetRequiredService<ContentSnapshotProvider>().Current;
        context.CacheVaryByRules.VaryByValues[GenerationKey] = snapshot.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.CacheVaryByRules.VaryByValues[QueryKey] = varyByCatalogQuery ? CanonicalCatalogQuery(http, snapshot) : string.Empty;
        context.ResponseExpirationTimeSpan = Lifetime;
        context.Tags.Add(OutputCacheInvalidator.ContentTag);
        return ValueTask.CompletedTask;
    }

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellation) => ValueTask.CompletedTask;

    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        HttpResponse response = context.HttpContext.Response;
        if (response.StatusCode != StatusCodes.Status200OK || !StringValues.IsNullOrEmpty(response.Headers.SetCookie))
        {
            context.AllowCacheStorage = false;
        }

        return ValueTask.CompletedTask;
    }

    private static bool IsCacheableRequest(HttpRequest request) =>
        (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        && !request.Cookies.ContainsKey(AuthCookieNames.Session)
        && !request.Headers.ContainsKey(HeaderNames.Authorization)
        && request.HttpContext.User.Identity?.IsAuthenticated != true;

    private static string CanonicalCatalogQuery(HttpContext http, ContentSnapshot snapshot)
    {
        RouteValueDictionary values = http.Request.RouteValues;
        if (!CultureKey.TryCreate(values[CultureAndStackMiddleware.CultureRouteKey] as string, out CultureKey culture)
            || !StackKey.TryCreate(values[CultureAndStackMiddleware.StackRouteKey] as string, out StackKey stack))
        {
            return string.Empty;
        }

        HashSet<TopicKey> knownTopics = [.. snapshot.TopicsOf(stack.Value).Select(t => new TopicKey(t))];
        CatalogQuery query = CatalogQueryParser.Parse(culture, stack, http.Request.Query, knownTopics);
        return CatalogQueryParser.ToCanonicalQueryString(query);
    }
}
