using Trainer.Domain.Keys;

namespace Trainer.Web.Routing;

/// <summary>Pure helpers for the culture segment, the first URL segment of every public page.</summary>
public static class CulturePath
{
    /// <summary>
    /// Rewrites <paramref name="pathAndQuery"/> so it points to the same page in <paramref name="targetCulture"/>.
    /// A path without a supported culture segment (the 404 page, for example) maps to the culture's home page.
    /// </summary>
    public static string SwitchCulture(string pathAndQuery, CultureKey targetCulture)
    {
        ArgumentNullException.ThrowIfNull(pathAndQuery);

        int queryStart = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        string path = queryStart < 0 ? pathAndQuery : pathAndQuery[..queryStart];
        string query = queryStart < 0 ? string.Empty : pathAndQuery[queryStart..];

        string trimmed = path.TrimStart('/');
        int slash = trimmed.IndexOf('/', StringComparison.Ordinal);
        string first = slash < 0 ? trimmed : trimmed[..slash];
        if (!CultureKey.TryCreate(first, out _))
        {
            return "/" + targetCulture.Value;
        }

        string rest = slash < 0 ? string.Empty : trimmed[slash..];
        return "/" + targetCulture.Value + rest + query;
    }

    /// <summary>The culture segment of an absolute path, or <see langword="null"/> when it is missing or unsupported.</summary>
    public static CultureKey? FromPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        string trimmed = path.TrimStart('/');
        int slash = trimmed.IndexOf('/', StringComparison.Ordinal);
        string first = slash < 0 ? trimmed : trimmed[..slash];
        return CultureKey.TryCreate(first, out CultureKey culture) ? culture : null;
    }
}
