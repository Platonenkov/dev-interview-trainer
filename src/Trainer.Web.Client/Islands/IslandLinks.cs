using Microsoft.AspNetCore.Components;
using Trainer.Web.Client.Interop;

namespace Trainer.Web.Client.Islands;

/// <summary>
/// Builds the links an island renders. Island pages carry a <c>&lt;base href&gt;</c> holding the site's path base, so every
/// link is relative to it and never starts with a slash. The static edition is served from folders (<c>index.html</c>), so
/// its page links end in a slash; the server edition keeps its canonical slashless form.
/// </summary>
public sealed class IslandLinks(ClientEdition edition)
{
    private readonly bool _trailingSlash = edition == ClientEdition.Static;

    public string Page(string culture, string stack, IReadOnlyList<string> segments, string? query = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        string path = string.Join('/', new[] { culture, stack }.Concat(segments));
        if (_trailingSlash)
        {
            path += "/";
        }

        return string.IsNullOrEmpty(query) ? path : path + "?" + query;
    }

    /// <summary>
    /// The current page with <c>_r=&lt;epoch milliseconds&gt;</c> set (an earlier <c>_r</c> is replaced). No parser reads the
    /// parameter; it only makes the browser and the CDN fetch the page and its content afresh after the site was updated.
    /// </summary>
    public static string ReloadUri(NavigationManager navigation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        return navigation.GetUriWithQueryParameter("_r", now.ToUnixTimeMilliseconds());
    }
}
