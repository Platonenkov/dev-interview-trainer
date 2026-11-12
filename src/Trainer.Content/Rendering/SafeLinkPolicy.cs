using System.Net;
using System.Text.RegularExpressions;

namespace Trainer.Content.Rendering;

/// <summary>
/// Decides which link and image targets from untrusted content Markdown may be rendered.
/// Only absolute <c>https:</c> URLs and site-relative paths are allowed.
/// </summary>
public static partial class SafeLinkPolicy
{
    // Bounds repeated decoding so that a pathological input cannot loop; each round peels one encoding layer.
    private const int MaxDecodeRounds = 4;

    /// <summary>Returns true for an absolute https URL or a site-relative path that is not protocol-relative.</summary>
    public static bool IsAllowed(string url)
    {
        // .NET decodes only HTML 4 entities, so any entity-shaped sequence is rejected instead of trusted:
        // an HTML5-only entity such as "&sol;" would otherwise slip through the decoding check below.
        if (string.IsNullOrEmpty(url) || ContainsForbiddenRawCharacter(url) || EntityLikeRegex().IsMatch(url))
        {
            return false;
        }

        if (!IsStructurallyAllowed(url))
        {
            return false;
        }

        // The browser and intermediate layers may decode entities or percent escapes, so every decoded form
        // must still be a safe target (for example "/%2Fevil.com" decodes to the protocol-relative "//evil.com").
        string current = url;
        for (int round = 0; round < MaxDecodeRounds; round++)
        {
            string decoded = Uri.UnescapeDataString(WebUtility.HtmlDecode(current));
            if (string.Equals(decoded, current, StringComparison.Ordinal))
            {
                return true;
            }

            if (ContainsControlCharacter(decoded) || !IsStructurallyAllowed(decoded))
            {
                return false;
            }

            current = decoded;
        }

        return false;
    }

    /// <summary>Returns true only for an absolute https URL with a host.</summary>
    public static bool IsAllowedAbsolute(string url) =>
        !string.IsNullOrEmpty(url)
        && !ContainsForbiddenRawCharacter(url)
        && IsAbsoluteHttps(url)
        && IsAllowed(url);

    private static bool IsStructurallyAllowed(string url) =>
        url.StartsWith('/') ? IsSiteRelative(url) : IsAbsoluteHttps(url);

    private static bool IsSiteRelative(string url) =>
        url.Length == 1 || (url[1] != '/' && url[1] != '\\');

    private static bool IsAbsoluteHttps(string url) =>
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
        && uri.Host.Length > 0;

    private static bool ContainsForbiddenRawCharacter(string url)
    {
        foreach (char c in url)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c) || c == '\\')
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsControlCharacter(string value)
    {
        foreach (char c in value)
        {
            if (char.IsControl(c))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex("&#?[A-Za-z0-9]+;")]
    private static partial Regex EntityLikeRegex();
}
