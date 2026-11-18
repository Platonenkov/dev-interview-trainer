using System.Text.RegularExpressions;

namespace Trainer.Web.StaticSite;

/// <summary>
/// The one rule that says which published files are content-addressed and therefore cacheable forever by the service
/// worker (ADR 0026). A name that matches is an immutable promise: the export verifier fails the build when a published
/// file matches and is not in the immutable set, so any worker may cache any build's file under that name. Host-only, not
/// <c>Trainer.Contracts</c>, which ships to the browser; the worker gets the same rule as a regex literal.
/// </summary>
public static partial class StaticAssetRules
{
    /// <summary>
    /// <c>name.&lt;fingerprint&gt;.ext</c>, the extension possibly compound (<c>.wasm.br</c>, <c>.modules.json</c>,
    /// <c>.lib.module.js</c>): a fingerprint is exactly 10 characters of <c>[a-z0-9]</c> between two dots
    /// (docs/static-edition-spike.md). Culture folders keep their folder.
    /// </summary>
    public const string FingerprintPattern = @".+\.[a-z0-9]{10}\.[A-Za-z0-9.]+";

    /// <summary>A content shard of ADR 0023: <c>data/{culture}/.../{name}.{16 hex}.json</c>.</summary>
    public const string ContentPattern = @"data/.+\.[0-9a-f]{16}\.json";

    private const string Alternatives = "(?:" + FingerprintPattern + "|" + ContentPattern + ")";

    // \z, not $: a trailing newline must not make a mutable path look immutable.
    [GeneratedRegex("^" + Alternatives + @"\z", RegexOptions.CultureInvariant)]
    private static partial Regex EligibleRegex();

    /// <summary>
    /// The rule as the body of a JavaScript regex literal. Slashes are escaped for the literal; <c>$</c> anchors the end
    /// because a request path never contains a line break.
    /// </summary>
    public static string JavaScriptRegex { get; } = "^" + Alternatives.Replace("/", @"\/", StringComparison.Ordinal) + "$";

    /// <summary>True when <paramref name="pathUnderScope"/> (no leading slash, no query) names an immutable file.</summary>
    public static bool IsEligible(string pathUnderScope)
    {
        ArgumentNullException.ThrowIfNull(pathUnderScope);
        return EligibleRegex().IsMatch(pathUnderScope);
    }
}
