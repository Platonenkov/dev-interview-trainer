using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace Trainer.StaticSite;

/// <summary>
/// Judges an exported directory the way a browser and GitHub Pages would, by parsing it instead of scanning text (spec 5.2): HTML
/// and XML are parsed, entities decoded, and every URL is resolved against the document and its <c>&lt;base&gt;</c> before the
/// prefix check. One problem fails the build.
/// </summary>
public static partial class ExportVerifier
{
    private static readonly string[] UrlAttributes = ["href", "src", "action", "formaction", "poster", "data", "cite", "manifest"];

    private const string FrameworkDirectory = "_framework/";

    // The files the page's loader decodes (islands-start.js): the boot resource types dotnetwasm, assembly, pdb, globalization.
    private static readonly string[] RuntimeExtensions = [".wasm", ".pdb", ".dat"];

    private static readonly string[] CssUrlGroups = ["a", "b", "c", "d", "e"];

    private static readonly string[] SourceSetAttributes = ["srcset", "imagesrcset"];

    private static readonly string[] ImageMetaNames =
        ["og:url", "og:image", "og:image:url", "og:image:secure_url", "og:video", "og:video:url", "og:audio", "og:audio:url", "twitter:image"];

    /// <summary>Every problem found in <paramref name="root"/>, empty for a site that is sound.</summary>
    /// <param name="root">The export directory.</param>
    /// <param name="siteUrl">The public site URL the files were exported for.</param>
    public static IReadOnlyList<string> Verify(string root, Uri siteUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(siteUrl);
        if (!Directory.Exists(root))
        {
            return [$"The directory '{root}' does not exist."];
        }

        return new Verification(Path.GetFullPath(root), siteUrl).Run();
    }

    [GeneratedRegex(@"url\(\s*(?:""(?<a>[^""]*)""|'(?<b>[^']*)'|(?<c>[^)\s]*))\s*\)|@import\s+(?:""(?<d>[^""]*)""|'(?<e>[^']*)')", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CssUrlPattern();

    [GeneratedRegex(@"^js/boot-integrity\.(?<hash>[a-z0-9]{10})\.mjs\z", RegexOptions.CultureInvariant)]
    private static partial Regex BootIntegrityRoute();

    [GeneratedRegex(@"^export default (?<json>\{.*\});\r?\n?\z", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex BootIntegrityModuleText();

    [GeneratedRegex(@"^(?<algorithm>sha256|sha384|sha512)-(?<hash>[A-Za-z0-9+/]+={0,2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex IntegrityPattern();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CssCommentPattern();

    [GeneratedRegex(@"url\s*=\s*['""]?(?<url>[^'""]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RefreshUrlPattern();

    private sealed class Verification
    {
        private readonly string _root;
        private readonly Uri _site;
        private readonly string _pathBase;
        private readonly HashSet<string> _files = new(StringComparer.Ordinal);
        private readonly HashSet<string> _stylesheets = new(StringComparer.Ordinal);
        private readonly List<string> _problems = [];
        private readonly Dictionary<(string File, string Algorithm), string> _hashes = [];
        private HashSet<string> _listed = new(StringComparer.Ordinal);
        private string? _buildId;
        private string? _google;
        private string? _yandex;

        public Verification(string root, Uri site)
        {
            _root = root;
            _site = site;
            _pathBase = SiteAddress.PathBaseOf(site);
            foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                _files.Add(Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'));
            }
        }

        public List<string> Run()
        {
            CheckContentAddressing();
            CheckBootIntegrity();
            string[] files = [.. _files.Order(StringComparer.Ordinal)];
            foreach (string file in files.Where(f => f.EndsWith(".html", StringComparison.Ordinal)))
            {
                CheckHtml(file);
            }

            foreach (string file in files.Where(f => f.EndsWith(".xml", StringComparison.Ordinal)))
            {
                CheckXml(file);
            }

            if (_files.Contains("robots.txt"))
            {
                CheckRobots();
            }

            foreach (string stylesheet in _stylesheets.Order(StringComparer.Ordinal))
            {
                CheckStylesheet(stylesheet);
            }

            return _problems;
        }

        // The loader decodes the published .br files in the page and checks each against js/boot-integrity.{hash}.mjs (ADR 0027).
        // A table that is missing, stale or wrong would not fail the site: every client would silently fetch the uncompressed
        // file as well, doubling the cold load, so the export fails instead.
        private void CheckBootIntegrity()
        {
            string[] tables = [.. _listed.Where(f => BootIntegrityRoute().IsMatch(f)).Order(StringComparer.Ordinal)];
            string[] compressed = [.. _listed.Where(IsCompressedRuntimeFile).Order(StringComparer.Ordinal)];
            if (tables.Length == 0)
            {
                if (compressed.Length > 0)
                {
                    Problem(compressed[0], "is a compressed runtime file, but the site has no integrity table (js/boot-integrity.{hash}.mjs), so the loader could not verify it.");
                }

                return;
            }

            foreach (string extra in tables.Skip(1))
            {
                Problem(extra, "is a second integrity table in this build; the loader imports one.");
            }

            Dictionary<string, string>? entries = ReadBootTable(tables[0]);
            if (entries is null)
            {
                return;
            }

            foreach ((string name, string integrity) in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                string file = FrameworkDirectory + name;
                if (!IsWellFormedIntegrity(integrity))
                {
                    Problem(tables[0], $"the integrity of '{name}' is not a well-formed sha256, sha384 or sha512 hash.");
                }
                else if (!_files.Contains(file))
                {
                    Problem(tables[0], $"lists '{file}', which is not in the site.");
                }
                else if (!IntegrityMatches(integrity, file))
                {
                    Problem(tables[0], $"the integrity of '{file}' does not match the bytes of the file.");
                }
            }

            foreach (string brotli in compressed)
            {
                CheckCompressedRuntimeFile(brotli, entries);
            }
        }

        private void CheckCompressedRuntimeFile(string brotli, Dictionary<string, string> entries)
        {
            string file = brotli[..^".br".Length];
            if (!entries.ContainsKey(file[FrameworkDirectory.Length..]))
            {
                Problem(brotli, "has no entry in the integrity table, so the loader would refuse its decoded bytes.");
            }

            if (!_files.Contains(file))
            {
                Problem(brotli, $"has no uncompressed '{file}' to fall back to.");
                return;
            }

            byte[] decoded;
            try
            {
                using MemoryStream source = new(File.ReadAllBytes(Path.Combine(_root, brotli)));
                using BrotliStream stream = new(source, CompressionMode.Decompress);
                using MemoryStream target = new();
                stream.CopyTo(target);
                decoded = target.ToArray();
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException)
            {
                Problem(brotli, $"is not a valid Brotli stream ({ex.Message}).");
                return;
            }

            if (!decoded.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(_root, file))))
            {
                Problem(brotli, $"does not decode to the bytes of '{file}'.");
            }
        }

        // The module is `export default {"name":"sha256-..."};`, the shape the loader imports. A file named by the hash of its
        // text must really have that hash, or a worker that cached the name would serve other bytes under it.
        private Dictionary<string, string>? ReadBootTable(string route)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(_root, route));
            string expectedName = Convert.ToHexStringLower(SHA256.HashData(bytes))[..10];
            if (!string.Equals(BootIntegrityRoute().Match(route).Groups["hash"].Value, expectedName, StringComparison.Ordinal))
            {
                Problem(route, "the name does not match its bytes.");
            }

            Match module = BootIntegrityModuleText().Match(new UTF8Encoding(false).GetString(bytes));
            if (!module.Success)
            {
                Problem(route, "is not `export default {...};`, the module the loader imports.");
                return null;
            }

            try
            {
                using JsonDocument json = JsonDocument.Parse(module.Groups["json"].Value);
                if (json.RootElement.ValueKind != JsonValueKind.Object || json.RootElement.EnumerateObject().Any(p => p.Value.ValueKind != JsonValueKind.String))
                {
                    Problem(route, "is not `export default {...};` with a string integrity for every name.");
                    return null;
                }

                return json.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
            }
            catch (JsonException ex)
            {
                Problem(route, $"is not `export default {{...}};` with valid JSON inside ({ex.Message}).");
                return null;
            }
        }

        private static bool IsCompressedRuntimeFile(string file) =>
            file.StartsWith(FrameworkDirectory, StringComparison.Ordinal)
            && file.EndsWith(".br", StringComparison.Ordinal)
            && RuntimeExtensions.Any(extension => file.EndsWith(extension + ".br", StringComparison.Ordinal));

        private static bool IsWellFormedIntegrity(string value)
        {
            Match match = IntegrityPattern().Match(value);
            if (!match.Success)
            {
                return false;
            }

            int length = match.Groups["algorithm"].Value switch { "sha256" => 32, "sha384" => 48, _ => 64 };
            Span<byte> decoded = stackalloc byte[64];
            return Convert.TryFromBase64String(match.Groups["hash"].Value, decoded, out int written) && written == length;
        }

        // The integrity of a site file, as a browser computes it: the hash of its bytes in the algorithm the value names.
        private bool IntegrityMatches(string value, string file)
        {
            string algorithm = IntegrityPattern().Match(value).Groups["algorithm"].Value;
            if (!_hashes.TryGetValue((file, algorithm), out string? expected))
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(_root, file));
                byte[] hash = algorithm switch { "sha256" => SHA256.HashData(bytes), "sha384" => SHA384.HashData(bytes), _ => SHA512.HashData(bytes) };
                expected = algorithm + "-" + Convert.ToBase64String(hash);
                _hashes[(file, algorithm)] = expected;
            }

            return string.Equals(value, expected, StringComparison.Ordinal);
        }

        private void Problem(string file, string message) => _problems.Add($"{file}: {message}");

        private string ReadText(string relative) => File.ReadAllText(Path.Combine(_root, relative), new UTF8Encoding(false));

        private T? ReadJson<T>(string relative, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, bool required)
            where T : class
        {
            if (!_files.Contains(relative))
            {
                if (required)
                {
                    Problem(relative, "is missing.");
                }

                return null;
            }

            try
            {
                return JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(_root, relative)), info);
            }
            catch (JsonException ex)
            {
                Problem(relative, $"is not valid JSON ({ex.Message}).");
                return null;
            }
        }

        // The worker caches by name across builds, so the rule is sound only if a file that matches it is a promised-immutable
        // one: listed by this build, or retained from the previous one (where it was listed).
        private void CheckContentAddressing()
        {
            BuildInfo? build = ReadJson("build.json", ExportJsonContext.Default.BuildInfo, required: true);
            _buildId = build?.BuildId;
            _google = build?.GoogleSiteVerification;
            _yandex = build?.YandexVerification;
            ImmutableInfo? immutable = ReadJson("immutable.json", ExportJsonContext.Default.ImmutableInfo, required: true);
            RetainedInfo? retained = ReadJson("retained.json", ExportJsonContext.Default.RetainedInfo, required: false);
            if (build is not null && immutable is not null && !string.Equals(build.BuildId, immutable.BuildId, StringComparison.Ordinal))
            {
                Problem("immutable.json", $"carries build id '{immutable.BuildId}' but build.json carries '{build.BuildId}'.");
            }

            if (immutable is null)
            {
                return;
            }

            _listed = new HashSet<string>(immutable.Files, StringComparer.Ordinal);
            Regex? eligible = CompileEligibility(immutable.EligiblePattern);
            HashSet<string> listed = new(immutable.Files, StringComparer.Ordinal);
            HashSet<string> kept = new(retained?.Files ?? [], StringComparer.Ordinal);
            foreach (string name in immutable.Files.Order(StringComparer.Ordinal).Where(n => !_files.Contains(n)))
            {
                Problem("immutable.json", $"lists '{name}', which is not in the site.");
            }

            foreach (string name in kept.Order(StringComparer.Ordinal).Where(n => !_files.Contains(n)))
            {
                Problem("retained.json", $"lists '{name}', which is not in the site.");
            }

            if (eligible is null)
            {
                return;
            }

            foreach (string file in _files.Order(StringComparer.Ordinal))
            {
                if (eligible.IsMatch(file) && !listed.Contains(file) && !kept.Contains(file))
                {
                    Problem(
                        file,
                        "matches the service worker's content-addressed name rule but is not in immutable.json, so a worker would cache it forever.");
                }
            }
        }

        private Regex? CompileEligibility(string? pattern)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                Problem("immutable.json", "carries no eligiblePattern.");
                return null;
            }

            try
            {
                return EligibilityRule.Compile(pattern);
            }
            catch (ArgumentException ex)
            {
                Problem("immutable.json", $"carries an eligiblePattern that is not a valid regex ({ex.Message}).");
                return null;
            }
        }

        private Uri DocumentUrl(string file)
        {
            string path = file == StaticSiteLayout.IndexFile
                ? "/"
                : file.EndsWith("/" + StaticSiteLayout.IndexFile, StringComparison.Ordinal) ? "/" + file[..^StaticSiteLayout.IndexFile.Length] : "/" + file;
            return new Uri(_site.GetLeftPart(UriPartial.Authority) + _pathBase + path);
        }

        private bool IsFile(string relative) => _files.Contains(relative);

        private void CheckHtml(string file)
        {
            IHtmlDocument document = new HtmlParser().ParseDocument(ReadText(file));
            Uri documentUrl = DocumentUrl(file);
            Uri baseUrl = documentUrl;
            bool hasBase = false;
            if (document.QuerySelector("base[href]") is { } baseElement)
            {
                string raw = baseElement.GetAttribute("href")!;
                CheckUrl(file, "base href", raw, documentUrl, hasBase: false, anchor: false, rel: null, element: "base");
                if (Uri.TryCreate(documentUrl, raw.Trim(), out Uri? resolved))
                {
                    baseUrl = resolved;
                    hasBase = true;
                }
            }

            CheckHead(file, document);
            Context context = new(file, baseUrl, hasBase);
            foreach (IElement element in document.All)
            {
                CheckElement(context, element);
            }

            foreach (IComment comment in Comments(document))
            {
                CheckComment(context, comment.Data);
            }

            CheckBootTableMap(context, document);
        }

        // islands-start.js imports the integrity table by its plain name, and only the page's import map can turn that into the
        // table's content-addressed file and let the browser verify it. A page that boots the Brotli runtime (data-brotli) without
        // the mapping would make the loader refuse every decoded file and fetch each one twice, so the export fails instead.
        private void CheckBootTableMap(Context context, IHtmlDocument document)
        {
            if (!document.QuerySelectorAll("script[data-brotli]").Any(s => string.Equals(s.GetAttribute("data-brotli"), "true", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            string? mapped = null;
            HashSet<string> withIntegrity = new(StringComparer.Ordinal);
            foreach (IElement script in document.QuerySelectorAll("script[type=importmap]"))
            {
                try
                {
                    using JsonDocument map = JsonDocument.Parse(script.TextContent);
                    foreach (JsonProperty entry in ObjectMembers(map.RootElement, "imports").Where(e => e.Value.ValueKind == JsonValueKind.String))
                    {
                        if (ResolvePath(context.Base, entry.Name) is { } key && key.EndsWith("/js/boot-integrity.mjs", StringComparison.Ordinal))
                        {
                            mapped = ResolveSiteFile(context.Base, entry.Value.GetString()!) ?? "(not a file of the site)";
                        }
                    }

                    foreach (JsonProperty entry in ObjectMembers(map.RootElement, "integrity").Where(e => e.Value.ValueKind == JsonValueKind.String && IsWellFormedIntegrity(e.Value.GetString()!)))
                    {
                        if (ResolveSiteFile(context.Base, entry.Name) is { } target)
                        {
                            withIntegrity.Add(target);
                        }
                    }
                }
                catch (JsonException)
                {
                    // The import map check reports a map that is not JSON.
                }
            }

            if (mapped is null)
            {
                Problem(context.File, "boots the Brotli runtime (data-brotli), but its import map does not map js/boot-integrity.mjs to the integrity table, so the loader would refuse every decoded file.");
            }
            else if (!BootIntegrityRoute().IsMatch(mapped))
            {
                Problem(context.File, $"maps js/boot-integrity.mjs to '{mapped}', which is not the integrity table.");
            }
            else if (!withIntegrity.Contains(mapped))
            {
                Problem(context.File, $"boots the Brotli runtime, but its import map carries no integrity for '{mapped}', so the browser would not verify the table.");
            }
        }

        private static JsonProperty[] ObjectMembers(JsonElement root, string section) =>
            root.ValueKind == JsonValueKind.Object && root.TryGetProperty(section, out JsonElement value) && value.ValueKind == JsonValueKind.Object
                ? [.. value.EnumerateObject()]
                : [];

        // The absolute path a URL names on this site, or null for anything else; reports nothing, unlike CheckUrl.
        private string? ResolvePath(Uri baseUrl, string raw)
        {
            if (!Uri.TryCreate(baseUrl, raw.Trim(), out Uri? uri) || !SameHost(uri))
            {
                return null;
            }

            string path = Uri.UnescapeDataString(uri.AbsolutePath);
            return SiteAddress.IsUnder(path, _pathBase) ? path : null;
        }

        private string? ResolveSiteFile(Uri baseUrl, string raw) =>
            ResolvePath(baseUrl, raw) is { } path && StaticSiteLayout.Resolve(path, _pathBase, IsFile) is { Status: 200 } resolution
                ? resolution.File
                : null;

        private void CheckHead(string file, IHtmlDocument document)
        {
            IElement? first = document.Head is { Children.Length: > 0 } head ? head.Children[0] : null;
            string? policy = first is not null
                && string.Equals(first.LocalName, "meta", StringComparison.Ordinal)
                && string.Equals(first.GetAttribute("http-equiv"), "Content-Security-Policy", StringComparison.OrdinalIgnoreCase)
                    ? first.GetAttribute("content")
                    : null;
            if (string.IsNullOrWhiteSpace(policy))
            {
                Problem(file, "the Content-Security-Policy meta is not the first element of <head>.");
            }

            CheckVerificationTags(file, document);
            string? build = document.QuerySelector("meta[name='dit-build']")?.GetAttribute("content");
            if (_buildId is not null && !string.Equals(build, _buildId, StringComparison.Ordinal))
            {
                Problem(file, $"the dit-build meta is '{build ?? "(missing)"}' but build.json says '{_buildId}'.");
            }

            bool redirectPage = document.QuerySelector("meta[http-equiv]") is not null
                && document.QuerySelectorAll("meta[http-equiv]").Any(m => string.Equals(m.GetAttribute("http-equiv"), "refresh", StringComparison.OrdinalIgnoreCase));
            if (!redirectPage)
            {
                CheckRecoveryScript(file, document, policy);
            }
        }

        // Search consoles ask for the tags on the front pages only (spec 5.10): the root chooser and each culture home page.
        private void CheckVerificationTags(string file, IHtmlDocument document)
        {
            bool front = file == StaticSiteLayout.IndexFile || (file.Length == "xx/index.html".Length && file.EndsWith("/index.html", StringComparison.Ordinal) && !file.Contains("//", StringComparison.Ordinal));
            CheckTag(file, document, "google-site-verification", _google, front);
            CheckTag(file, document, "yandex-verification", _yandex, front);
        }

        private void CheckTag(string file, IHtmlDocument document, string name, string? configured, bool front)
        {
            string[] found = [.. document.QuerySelectorAll($"meta[name='{name}']").Select(m => m.GetAttribute("content") ?? string.Empty)];
            if (front && configured is not null)
            {
                if (found.Length != 1 || !string.Equals(found[0], configured, StringComparison.Ordinal))
                {
                    Problem(file, $"the {name} meta is missing or does not carry the configured value.");
                }
            }
            else if (found.Length > 0)
            {
                Problem(file, front ? $"the {name} meta is present but none is configured." : $"the {name} meta belongs on the front pages only.");
            }
        }

        // Inline and allowed by hash, so it works when the very file that failed to load is the startup script.
        private void CheckRecoveryScript(string file, IHtmlDocument document, string? policy)
        {
            IElement? script = document.QuerySelectorAll("script:not([src])")
                .FirstOrDefault(s => s.TextContent.Contains("window.ditRecover", StringComparison.Ordinal));
            if (script is null)
            {
                Problem(file, "the page does not carry the recovery script.");
                return;
            }

            string source = "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script.TextContent))) + "'";
            if (policy is not null && !policy.Contains(source, StringComparison.Ordinal))
            {
                Problem(file, "the recovery script is not allowed by the page's Content-Security-Policy hash.");
            }
        }

        private void CheckElement(Context context, IElement element)
        {
            string name = element.LocalName;
            string? rel = element.GetAttribute("rel");
            bool anchor = name is "a" or "area";
            if (name != "base")
            {
                foreach (string attribute in UrlAttributes)
                {
                    if (element.GetAttribute(attribute) is { } value)
                    {
                        string? resolved = CheckUrl(context.File, $"{name}[{attribute}]", value, context.Base, context.HasBase, anchor, rel, name);
                        if (name == "link" && attribute == "href" && resolved is not null && HasToken(rel, "stylesheet") && resolved.EndsWith(".css", StringComparison.Ordinal))
                        {
                            _stylesheets.Add(resolved);
                        }
                    }
                }
            }

            foreach (string attribute in SourceSetAttributes)
            {
                if (element.GetAttribute(attribute) is { } set)
                {
                    foreach (string candidate in set.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        string url = candidate.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
                        CheckUrl(context.File, $"{name}[{attribute}]", url, context.Base, context.HasBase, anchor: false, rel, name);
                    }
                }
            }

            if (element.GetAttribute("style") is { } style)
            {
                CheckCss(context.File, "style attribute", style, context.Base);
            }

            switch (name)
            {
                case "style":
                    CheckCss(context.File, "<style>", element.TextContent, context.Base);
                    break;
                case "meta":
                    CheckMeta(context, element);
                    break;
                case "script":
                    CheckScriptData(context, element);
                    break;
            }
        }

        private void CheckMeta(Context context, IElement meta)
        {
            string? content = meta.GetAttribute("content");
            if (content is null)
            {
                return;
            }

            string? key = meta.GetAttribute("property") ?? meta.GetAttribute("name");
            if (key is not null && ImageMetaNames.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                CheckUrl(context.File, $"meta {key}", content, context.Base, context.HasBase, anchor: false, rel: null, element: "meta");
            }

            if (string.Equals(meta.GetAttribute("http-equiv"), "refresh", StringComparison.OrdinalIgnoreCase)
                && RefreshUrlPattern().Match(content) is { Success: true } refresh)
            {
                CheckUrl(context.File, "meta refresh", refresh.Groups["url"].Value, context.Base, context.HasBase, anchor: false, rel: null, element: "meta");
            }
        }

        private void CheckScriptData(Context context, IElement script)
        {
            string? type = script.GetAttribute("type");
            if (string.Equals(type, "importmap", StringComparison.OrdinalIgnoreCase))
            {
                CheckImportMap(context, script.TextContent);
            }
            else if (string.Equals(type, "application/ld+json", StringComparison.OrdinalIgnoreCase))
            {
                CheckJsonLd(context, script.TextContent);
            }
        }

        private void CheckImportMap(Context context, string json)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                foreach (JsonProperty section in document.RootElement.EnumerateObject())
                {
                    if (section.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    foreach (JsonProperty entry in section.Value.EnumerateObject())
                    {
                        if (string.Equals(section.Name, "integrity", StringComparison.Ordinal))
                        {
                            // {"<url>": "sha256-<hash>"}: the key names a file; the value is the hash that file must have.
                            string? target = CheckUrl(context.File, "import map integrity key", entry.Name, context.Base, context.HasBase, anchor: false, rel: null, element: "script");
                            CheckImportMapIntegrity(context.File, entry, target);
                            continue;
                        }

                        if (LooksLikeUrl(entry.Name))
                        {
                            // A key is only matched against the specifier a script imports; nothing fetches it, so it need not be a file
                            // (the boot integrity table is imported as a plain name that the map turns into its fingerprinted file).
                            CheckUrl(context.File, $"import map {section.Name} key", entry.Name, context.Base, context.HasBase, anchor: false, rel: null, element: "script", mustExist: false);
                        }

                        if (entry.Value.ValueKind == JsonValueKind.String)
                        {
                            CheckUrl(context.File, $"import map {section.Name}", entry.Value.GetString()!, context.Base, context.HasBase, anchor: false, rel: null, element: "script");
                        }
                        else if (entry.Value.ValueKind == JsonValueKind.Object)
                        {
                            foreach (JsonProperty scoped in entry.Value.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String))
                            {
                                CheckUrl(context.File, "import map scope", scoped.Value.GetString()!, context.Base, context.HasBase, anchor: false, rel: null, element: "script");
                            }
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                Problem(context.File, $"the import map is not valid JSON ({ex.Message}).");
            }
        }

        private void CheckImportMapIntegrity(string page, JsonProperty entry, string? targetFile)
        {
            string? value = entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString() : null;
            if (value is null || !IsWellFormedIntegrity(value))
            {
                Problem(page, $"the import map integrity of {entry.Name} is not a well-formed sha256, sha384 or sha512 hash.");
            }
            else if (targetFile is not null && !IntegrityMatches(value, targetFile))
            {
                Problem(page, $"the import map integrity of {entry.Name} does not match the bytes of '{targetFile}'.");
            }
        }

        // JSON-LD names the page and its parts by absolute URL; only the ones on this host are held to the site's rules.
        private void CheckJsonLd(Context context, string json)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                WalkStrings(document.RootElement, value =>
                {
                    if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && SameHost(uri))
                    {
                        CheckUrl(context.File, "JSON-LD", value, context.Base, context.HasBase, anchor: false, rel: null, element: "script");
                    }
                });
            }
            catch (JsonException ex)
            {
                Problem(context.File, $"the JSON-LD block is not valid JSON ({ex.Message}).");
            }
        }

        private static void WalkStrings(JsonElement element, Action<string> visit)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    visit(element.GetString()!);
                    break;
                case JsonValueKind.Array:
                    foreach (JsonElement item in element.EnumerateArray())
                    {
                        WalkStrings(item, visit);
                    }

                    break;
                case JsonValueKind.Object:
                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        WalkStrings(property.Value, visit);
                    }

                    break;
            }
        }

        private void CheckCss(string file, string where, string css, Uri baseUrl)
        {
            foreach (Match match in CssUrlPattern().Matches(CssCommentPattern().Replace(css, string.Empty)))
            {
                string value = CssUrlGroups.Select(g => match.Groups[g]).First(g => g.Success).Value;
                CheckUrl(file, $"css url() in {where}", value, baseUrl, hasBase: false, anchor: false, rel: null, element: "style");
            }
        }

        private void CheckStylesheet(string file)
        {
            CheckCss(file, "the stylesheet", ReadText(file), DocumentUrl(file));
        }

        private void CheckXml(string file)
        {
            XDocument document;
            try
            {
                // No DTD and no resolver: a sitemap has neither, and an external entity must never be fetched.
                using XmlReader reader = XmlReader.Create(
                    new StringReader(ReadText(file)),
                    new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                document = XDocument.Load(reader);
            }
            catch (XmlException ex)
            {
                Problem(file, $"is not well-formed XML ({ex.Message}).");
                return;
            }

            Uri documentUrl = DocumentUrl(file);
            foreach (XElement element in document.Descendants())
            {
                if (element.Name.LocalName == "loc")
                {
                    CheckAbsolute(file, "sitemap loc", element.Value.Trim(), documentUrl);
                }
                else if (element.Name.LocalName == "link" && element.Attribute("href") is { } href)
                {
                    CheckAbsolute(file, "xhtml:link href", href.Value, documentUrl);
                }
            }
        }

        private void CheckRobots()
        {
            foreach (string line in ReadText("robots.txt").Split('\n'))
            {
                if (line.StartsWith("Sitemap:", StringComparison.OrdinalIgnoreCase))
                {
                    CheckAbsolute("robots.txt", "Sitemap", line["Sitemap:".Length..].Trim(), DocumentUrl("robots.txt"));
                }
            }
        }

        private void CheckAbsolute(string file, string where, string value, Uri baseUrl)
        {
            // On Unix a rooted path such as "/ru/" parses as an absolute file: URI, so the scheme is part of the check.
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? absolute)
                || (absolute.Scheme != Uri.UriSchemeHttp && absolute.Scheme != Uri.UriSchemeHttps))
            {
                Problem(file, $"{where} '{value}' is not an absolute http(s) URL.");
                return;
            }

            CheckUrl(file, where, value, baseUrl, hasBase: false, anchor: false, rel: null, element: "xml");
        }

        private void CheckComment(Context context, string text) => StateScanner.Scan(this, context, text);

        private static IEnumerable<IComment> Comments(INode node)
        {
            foreach (INode child in node.ChildNodes)
            {
                if (child is IComment comment)
                {
                    yield return comment;
                }

                foreach (IComment nested in Comments(child))
                {
                    yield return nested;
                }
            }
        }

        private static bool HasToken(string? value, string token) =>
            value is not null && value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(token, StringComparer.OrdinalIgnoreCase);

        private static bool LooksLikeUrl(string value) =>
            value.StartsWith('/') || value.StartsWith("./", StringComparison.Ordinal) || value.StartsWith("../", StringComparison.Ordinal)
            || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        private bool SameHost(Uri uri) =>
            string.Equals(uri.Host, _site.Host, StringComparison.OrdinalIgnoreCase) && uri.Port == _site.Port;

        /// <summary>Checks one URL; returns the site-relative file it resolves to, or null when it is not a site file.</summary>
        private string? CheckUrl(string file, string where, string raw, Uri baseUrl, bool hasBase, bool anchor, string? rel, string element, bool mustExist = true)
        {
            string value = raw.Trim(' ', '\t', '\r', '\n', '\f');
            if (value.Length == 0)
            {
                return null;
            }

            if (value.StartsWith('#'))
            {
                if (hasBase && element is "a" or "area")
                {
                    // On an island page <base> is the site root, so a bare fragment would navigate away from the page.
                    Problem(file, $"{where}=\"{value}\" resolves against <base> and leaves the page.");
                }

                return null;
            }

            if (!Uri.TryCreate(baseUrl, value, out Uri? uri))
            {
                Problem(file, $"{where}=\"{value}\" is not a valid URL.");
                return null;
            }

            if (uri.Scheme is "mailto" or "tel" or "data" or "blob")
            {
                return null;
            }

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                Problem(file, $"{where}=\"{value}\" uses the scheme '{uri.Scheme}'.");
                return null;
            }

            if (!SameHost(uri))
            {
                if (!ExternalAllowed(uri, anchor, rel, element))
                {
                    Problem(file, $"{where}=\"{value}\" points at {uri.GetLeftPart(UriPartial.Authority)}, which the site may not load or link.");
                }

                return null;
            }

            string path = Uri.UnescapeDataString(uri.AbsolutePath);
            if (!SiteAddress.IsUnder(path, _pathBase))
            {
                Problem(file, $"{where}=\"{value}\" resolves to {path}, outside the site path '{_pathBase}/'.");
                return null;
            }

            if (uri.Scheme != _site.Scheme)
            {
                Problem(file, $"{where}=\"{value}\" uses {uri.Scheme} on the site's own host.");
            }

            StaticResolution resolution = StaticSiteLayout.Resolve(path, _pathBase, IsFile);
            if (resolution.Status == 200)
            {
                return resolution.File;
            }

            if (!mustExist)
            {
                return null;
            }

            Problem(
                file,
                resolution.Status == 301
                    ? $"{where}=\"{value}\" is a directory without a trailing slash; Pages would redirect it."
                    : $"{where}=\"{value}\" resolves to {path}, which is not in the site (404).");
            return null;
        }

        private static bool ExternalAllowed(Uri uri, bool anchor, string? rel, string element)
        {
            if (uri.Scheme == Uri.UriSchemeHttps
                && (string.Equals(uri.Host, "gc.zgo.at", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.EndsWith(".goatcounter.com", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            // The "report a problem" link, and the same URL in the island's bootstrap, which hands it to the island.
            if ((anchor || element == "state") && IsGitHubIssues(uri))
            {
                return true;
            }

            // Content source links: links only, always with rel=noopener.
            return anchor && HasToken(rel, "noopener");
        }

        private static bool IsGitHubIssues(Uri uri) =>
            uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Contains("/issues", StringComparison.Ordinal);

        private readonly record struct Context(string File, Uri Base, bool HasBase);

        // The persisted component state and the island markers carry parameters as base64 JSON; a URL in there outside the
        // prefix would surface only when the island starts.
        private static class StateScanner
        {
            private const int MaxDepth = 5;

            public static void Scan(Verification owner, Context context, string comment)
            {
                int colon = comment.IndexOf(':', StringComparison.Ordinal);
                if (!comment.StartsWith("Blazor", StringComparison.Ordinal) || colon < 0)
                {
                    return;
                }

                ScanText(owner, context, comment[(colon + 1)..], 0);
            }

            private static void ScanText(Verification owner, Context context, string text, int depth)
            {
                string trimmed = text.Trim();
                if (trimmed.Length == 0 || depth > MaxDepth)
                {
                    return;
                }

                if (trimmed[0] is '{' or '[')
                {
                    try
                    {
                        using JsonDocument document = JsonDocument.Parse(trimmed);
                        Walk(owner, context, document.RootElement, depth);
                    }
                    catch (JsonException)
                    {
                        // Not JSON after all: nothing in it names a URL the page would follow.
                    }

                    return;
                }

                if (TryDecodeBase64(trimmed, out string? decoded))
                {
                    ScanText(owner, context, decoded, depth + 1);
                }
            }

            private static void Walk(Verification owner, Context context, JsonElement element, int depth)
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.String:
                        ScanString(owner, context, element.GetString()!, depth);
                        break;
                    case JsonValueKind.Array:
                        foreach (JsonElement item in element.EnumerateArray())
                        {
                            Walk(owner, context, item, depth);
                        }

                        break;
                    case JsonValueKind.Object:
                        foreach (JsonProperty property in element.EnumerateObject())
                        {
                            Walk(owner, context, property.Value, depth);
                        }

                        break;
                }
            }

            private static void ScanString(Verification owner, Context context, string value, int depth)
            {
                // The island's bootstrap is a JSON document carried as one string parameter.
                if (value.AsSpan().TrimStart().StartsWith("{") || value.AsSpan().TrimStart().StartsWith("["))
                {
                    ScanText(owner, context, value, depth + 1);
                    return;
                }

                if (LooksLikeUrl(value) && !value.StartsWith("./", StringComparison.Ordinal) && !value.StartsWith("../", StringComparison.Ordinal))
                {
                    owner.CheckUrl(context.File, "persisted state", value, context.Base, context.HasBase, anchor: false, rel: null, element: "state");
                    return;
                }

                if (value.StartsWith("data/", StringComparison.Ordinal) && value.EndsWith(".json", StringComparison.Ordinal))
                {
                    owner.CheckUrl(context.File, "persisted state", value, context.Base, context.HasBase, anchor: false, rel: null, element: "state");
                    return;
                }

                if (value.Length >= 8 && TryDecodeBase64(value, out string? decoded))
                {
                    ScanText(owner, context, decoded, depth + 1);
                }
            }

            private static bool TryDecodeBase64(string value, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? decoded)
            {
                decoded = null;
                if (value.Length % 4 != 0 || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=')))
                {
                    return false;
                }

                try
                {
                    decoded = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(Convert.FromBase64String(value));
                    return true;
                }
                catch (Exception ex) when (ex is FormatException or ArgumentException)
                {
                    return false;
                }
            }
        }
    }
}
