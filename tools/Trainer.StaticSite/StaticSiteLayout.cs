namespace Trainer.StaticSite;

/// <param name="Status">200, 301 or 404.</param>
/// <param name="File">The file to send, relative to the site root with forward slashes; <c>404.html</c> for a 404.</param>
/// <param name="Location">The redirect target of a 301.</param>
public sealed record StaticResolution(int Status, string? File, string? Location);

/// <summary>
/// How a page of the site maps to a file and how GitHub Pages maps a request back to one. The exporter and the emulator
/// share it with the verifier, so the verifier judges a link by the rule the host applies.
/// </summary>
public static class StaticSiteLayout
{
    public const string IndexFile = "index.html";
    public const string NotFoundFile = "404.html";

    /// <summary>
    /// The file a site path is saved as: a page URL with a trailing slash becomes <c>&lt;dir&gt;/index.html</c>, a file URL keeps its
    /// name. Paths are decoded; a path that leaves the site (or would leave the output directory) throws.
    /// </summary>
    /// <param name="sitePath">An absolute path under the path base, for example <c>/dit/ru/dotnet/</c>.</param>
    /// <param name="pathBase">Empty for a root site, <c>/dit</c> for a prefix: leading slash, no trailing one.</param>
    /// <exception cref="ArgumentException">The path is not under the path base or is not a plain relative file path.</exception>
    public static string FileFor(string sitePath, string pathBase)
    {
        ArgumentNullException.ThrowIfNull(sitePath);
        ArgumentNullException.ThrowIfNull(pathBase);
        string? rest = Strip(sitePath, pathBase);
        if (rest is null)
        {
            throw new ArgumentException($"'{sitePath}' is not under the path base '{pathBase}'.", nameof(sitePath));
        }

        string file = rest.Length == 0 || rest.EndsWith('/') ? rest + IndexFile : rest;
        if (!IsPlainRelativePath(file))
        {
            throw new ArgumentException($"'{sitePath}' is not a plain file path.", nameof(sitePath));
        }

        return file;
    }

    /// <summary>
    /// What Pages answers for <paramref name="requestPath"/> (decoded, no query): a file is sent; a directory with a trailing
    /// slash sends its <c>index.html</c>; a directory without one is a 301 to the slashed URL; anything else is a 404 whose
    /// body is <c>404.html</c>.
    /// </summary>
    /// <param name="exists">True when a file exists at a site-root-relative path such as <c>ru/dotnet/index.html</c>.</param>
    public static StaticResolution Resolve(string requestPath, string pathBase, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(requestPath);
        ArgumentNullException.ThrowIfNull(pathBase);
        ArgumentNullException.ThrowIfNull(exists);

        StaticResolution notFound = new(404, NotFoundFile, null);
        if (pathBase.Length > 0 && string.Equals(requestPath, pathBase, StringComparison.Ordinal))
        {
            return exists(IndexFile) ? new StaticResolution(301, null, pathBase + "/") : notFound;
        }

        string? rest = Strip(requestPath, pathBase);
        if (rest is null)
        {
            return notFound;
        }

        if (rest.Length == 0 || rest.EndsWith('/'))
        {
            string index = rest + IndexFile;
            return IsPlainRelativePath(index) && exists(index) ? new StaticResolution(200, index, null) : notFound;
        }

        if (!IsPlainRelativePath(rest))
        {
            return notFound;
        }

        if (exists(rest))
        {
            return new StaticResolution(200, rest, null);
        }

        return exists(rest + "/" + IndexFile) ? new StaticResolution(301, null, requestPath + "/") : notFound;
    }

    /// <summary>The path under the path base without its leading slash, or null when the path is not under the base.</summary>
    private static string? Strip(string path, string pathBase)
    {
        string prefix = pathBase + "/";
        return path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : null;
    }

    /// <summary>The directory without a trailing separator, so containment checks do not depend on how it was typed.</summary>
    internal static string NormalizeRoot(string directory) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

    /// <summary>True when <paramref name="fullPath"/> is below <paramref name="root"/> (a <see cref="NormalizeRoot"/> result).</summary>
    internal static bool IsInside(string root, string fullPath)
    {
        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.Ordinal);
    }

    internal static bool IsPlainRelativePath(string path)
    {
        if (path.Length == 0 || path.Contains('\\', StringComparison.Ordinal) || path.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (string segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.Any(char.IsControl))
            {
                return false;
            }
        }

        return true;
    }
}
