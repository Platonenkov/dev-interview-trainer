using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Components;

namespace Trainer.Web.StaticSite;

/// <summary>
/// The table the Brotli loader verifies decoded runtime files against (ADR 0027): boot resource name to <c>sha256-</c>
/// integrity of the file the export publishes, as an ES module (<c>export default {...}</c>) so the page imports it through
/// the import map like the decoder. The runtime itself hands the loader an empty integrity for fingerprinted resources
/// (<c>docs/performance.md</c>, "Brotli boot"), so without this table no decoded byte could be verified.
/// The file is content-addressed (<see cref="Route"/> carries a hash of its text), listed in the export and covered by the
/// import map's <c>integrity</c> section, so the page checks the table before it trusts it.
/// </summary>
public sealed class BootIntegrityModule
{
    /// <summary>The route the page imports; the import map turns it into <see cref="Route"/>.</summary>
    public const string PlainRoute = "js/boot-integrity.mjs";

    private const string RoutePrefix = "js/boot-integrity.";
    private const string RouteSuffix = ".mjs";
    private const int FingerprintLength = 10;

    // Relaxed escaping keeps '+' and '/' of a base64 hash readable; the keys and values are ASCII file names and hashes.
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private BootIntegrityModule(IReadOnlyDictionary<string, string> entries, string text)
    {
        Entries = entries;
        Text = text;
        byte[] hash = SHA256.HashData(Bytes);
        Integrity = "sha256-" + Convert.ToBase64String(hash);
        Route = RoutePrefix + Convert.ToHexStringLower(hash)[..FingerprintLength] + RouteSuffix;
    }

    /// <summary>Boot resource name (the fingerprinted file name under <c>_framework/</c>) to its <c>sha256-</c> integrity.</summary>
    public IReadOnlyDictionary<string, string> Entries { get; }

    /// <summary>The module source, sorted by name so the same files always give the same bytes.</summary>
    public string Text { get; }

    /// <summary>The UTF-8 bytes of <see cref="Text"/>, without a byte order mark.</summary>
    public byte[] Bytes => Encoding.UTF8.GetBytes(Text);

    /// <summary>The <c>sha256-</c> integrity of <see cref="Bytes"/>, the value of the import map's <c>integrity</c> entry.</summary>
    public string Integrity { get; }

    /// <summary>The content-addressed route, <c>js/boot-integrity.{10 characters}.mjs</c>.</summary>
    public string Route { get; }

    /// <summary>
    /// The import map entries of the table, in the shape <c>ImportMapDefinition.FromResourceCollection</c> writes (keys and
    /// values relative to <c>&lt;base href&gt;</c>): the plain name the loader imports, its content-addressed file, and the
    /// file's integrity so the browser verifies it.
    /// </summary>
    public ImportMapDefinition ToImportMap() => new(
        new Dictionary<string, string>(StringComparer.Ordinal) { ["./" + PlainRoute] = "./" + Route },
        scopes: null,
        new Dictionary<string, string>(StringComparer.Ordinal) { ["./" + Route] = Integrity });

    public static BootIntegrityModule Create(IReadOnlyDictionary<string, string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        SortedDictionary<string, string> sorted = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> entry in entries)
        {
            sorted.Add(entry.Key, entry.Value);
        }

        string text = "export default " + JsonSerializer.Serialize(sorted, Options) + ";\n";
        return new BootIntegrityModule(sorted, text);
    }
}
