using System.Security.Cryptography;
using System.Text;
using Trainer.Web.Analytics;

namespace Trainer.Web.Startup;

/// <summary>
/// The Content-Security-Policy of the static edition, served as a <c>&lt;meta http-equiv&gt;</c> element because GitHub
/// Pages cannot send headers (ADR 0024). A static file cannot carry a per-request nonce, so every inline script is
/// allowed by its SHA-256: the theme script, the recovery script and, on island pages, the import map. A new inline
/// script on a static page must be added here, or the browser refuses it.
/// </summary>
/// <remarks>
/// A meta policy cannot carry <c>frame-ancestors</c>, <c>report-uri</c> or <c>sandbox</c>; none of them appears here.
/// </remarks>
public static class StaticCsp
{
    // Lazy: RecoveryScript hashes through Sha256Source, so a field initialiser here would run in the middle of its own
    // static initialisation and read a null hash.
    private static readonly Lazy<string> FixedScriptHashes =
        new(() => Sha256Source(ThemeScript.StaticSource) + " " + RecoveryScript.CspSource);

    /// <summary>The policy for one page.</summary>
    /// <param name="islandPage">An island page: adds WebAssembly compilation, the import map and a same-origin base.</param>
    /// <param name="importMapHash">The <see cref="Sha256Source"/> of the page's import map text; required for an island page.</param>
    /// <param name="goatCounterCode">The GoatCounter site code, or null or empty when analytics is off (no origin is allowed then).</param>
    /// <exception cref="ArgumentException">An island page without an import map hash, or a malformed site code.</exception>
    public static string For(bool islandPage, string? importMapHash, string? goatCounterCode)
    {
        if (islandPage && string.IsNullOrEmpty(importMapHash))
        {
            throw new ArgumentException("An island page needs the hash of its import map.", nameof(importMapHash));
        }

        bool counting = !string.IsNullOrEmpty(goatCounterCode);
        if (counting && !GoatCounterScript.IsValidCode(goatCounterCode))
        {
            // The code ends up inside a policy, so it must never carry a semicolon or a space.
            throw new ArgumentException("The GoatCounter code is malformed.", nameof(goatCounterCode));
        }

        string scriptSources = "'self' "
            + (islandPage ? $"'wasm-unsafe-eval' {importMapHash} " : string.Empty)
            + FixedScriptHashes.Value;
        string connectSources = counting ? $"'self' {GoatCounterScript.Host(goatCounterCode!)}" : "'self'";

        return "default-src 'self'; " +
            $"script-src {scriptSources}; " +
            "style-src 'self'; " +
            "img-src 'self' https: data:; " +
            "font-src 'self'; " +
            $"connect-src {connectSources}; " +
            "worker-src 'self'; " +
            $"base-uri {(islandPage ? "'self'" : "'none'")}; " +
            "form-action 'self'";
    }

    /// <summary>The CSP source expression (<c>'sha256-...'</c>) that allows exactly <paramref name="inlineText"/>.</summary>
    public static string Sha256Source(string inlineText)
    {
        ArgumentNullException.ThrowIfNull(inlineText);
        return "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(inlineText))) + "'";
    }
}
