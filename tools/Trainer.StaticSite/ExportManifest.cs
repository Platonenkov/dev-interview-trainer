using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trainer.StaticSite;

/// <summary>
/// The app's <c>GET /static-export/manifest.json</c>, read with the tool's own record: the tool does not reference the web
/// project, so the two sides agree through this JSON shape and the integration test that exports a real site.
/// </summary>
public sealed record ExportManifest(
    IReadOnlyList<string> Pages,
    IReadOnlyList<string> Redirects,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Indexes,
    IReadOnlyList<string> Assets,
    IReadOnlyList<string> Immutable,
    string NotFoundPage,
    string RedirectPageCsp,
    string? BuildId,
    string EligiblePattern,
    string? GoogleSiteVerification = null,
    string? YandexVerification = null);

/// <summary>
/// <c>build.json</c>: which build a deployment is. The two verification values are the search-engine tags the pages were
/// rendered with (null when not configured), so <c>verify</c> can check the pages against what was configured.
/// </summary>
public sealed record BuildInfo(
    string BuildId,
    string Commit,
    DateTimeOffset BuiltAt,
    string? GoogleSiteVerification = null,
    string? YandexVerification = null);

/// <summary>
/// <c>immutable.json</c>: the build id, every content-addressed file of the deployment (site-relative, no prefix) and the
/// service worker's eligibility rule. The rule travels with the file so that <c>verify</c> judges a directory by the rule the
/// build was made with, without referencing the web project.
/// </summary>
public sealed record ImmutableInfo(string BuildId, IReadOnlyList<string> Files, string? EligiblePattern = null);

/// <summary>
/// <c>retained.json</c>: the files copied from the previous deployment (<see cref="Retention"/>). They are immutable names
/// that the new build does not list, so the verifier needs this record to tell them from a mutable file that merely
/// looks content-addressed.
/// </summary>
public sealed record RetainedInfo(string BuildId, IReadOnlyList<string> Files);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(ExportManifest))]
[JsonSerializable(typeof(BuildInfo))]
[JsonSerializable(typeof(ImmutableInfo))]
[JsonSerializable(typeof(RetainedInfo))]
public sealed partial class ExportJsonContext : JsonSerializerContext
{
    public static byte[] Serialize<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        [.. JsonSerializer.SerializeToUtf8Bytes(value, info), (byte)'\n'];
}

/// <summary>The service worker's eligibility rule, compiled from the JavaScript regex body the app publishes.</summary>
internal static class EligibilityRule
{
    /// <exception cref="ArgumentException">The pattern is not a valid regex.</exception>
    public static System.Text.RegularExpressions.Regex Compile(string pattern)
    {
        // The rule is a JavaScript regex literal body: escaped slashes, and $ because a request path has no line break.
        string body = pattern.Replace("\\/", "/", StringComparison.Ordinal);
        body = body.StartsWith('^') ? body[1..] : body;
        body = body.EndsWith('$') ? body[..^1] : body;
        return new System.Text.RegularExpressions.Regex(
            "^(?:" + body + @")\z",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(5));
    }
}

/// <summary>A failure the tool reports as exit code 1 with its message.</summary>
public sealed class ExportFailedException : Exception
{
    public ExportFailedException(string message)
        : base(message)
    {
    }

    public ExportFailedException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
