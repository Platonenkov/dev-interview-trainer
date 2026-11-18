using Trainer.Domain.Keys;
using Trainer.Web.Routing;

namespace Trainer.Web.Seo;

/// <summary>
/// The x-default alternate of a page. The site root picks a language for the visitor (the static edition's chooser, the
/// server's redirect), so it is the x-default of the culture home pages; every other page falls back to its English
/// version, or to its only culture.
/// </summary>
public static class HreflangDefault
{
    public const string RootPath = "/";

    /// <summary>The logical path of the x-default alternate for <paramref name="fallbackPath"/>, the page in its default culture.</summary>
    public static string PathFor(string fallbackPath)
    {
        ArgumentNullException.ThrowIfNull(fallbackPath);
        string trimmed = fallbackPath.Trim('/');
        return CultureKey.TryCreate(trimmed, out _) ? RootPath : fallbackPath;
    }
}
