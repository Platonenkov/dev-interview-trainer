using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Trainer.StaticSite.Tests;

public sealed class ExportVerifierTests
{
    private static string ProblemsText(IReadOnlyList<string> problems) => string.Join(Environment.NewLine, problems);

    [Fact]
    public void AcceptsSlashedPagesAssetsQueryStringsAndGoatCounter()
    {
        using SiteDir site = SiteDir.Valid();

        IReadOnlyList<string> problems = site.Verify();

        Assert.True(problems.Count == 0, ProblemsText(problems));
    }

    [Fact]
    public void AcceptsARootSite()
    {
        using SiteDir site = new();
        site.Write("index.html", TestPages.Html("<a href=\"/ru/\">ru</a><a href=\"ru/\">ru</a>", "<link rel=\"stylesheet\" href=\"/css/a.abcdefghij.css\">"));
        site.Write("ru/index.html", TestPages.Html("<a href=\"/\">home</a>"));
        site.Write("404.html", TestPages.Html("<p>no</p>"));
        site.Write("css/a.abcdefghij.css", "a{background:url(/css/a.abcdefghij.css)}");
        site.WriteManifests(["css/a.abcdefghij.css"]);

        IReadOnlyList<string> problems = site.Verify(new Uri("https://example.test/"));

        Assert.True(problems.Count == 0, ProblemsText(problems));
    }

    [Fact]
    public void FindsARootRelativeUrlWithoutThePrefix()
    {
        using SiteDir site = SiteDir.Valid();
        site.Write("ru/index.html", TestPages.Html("<a href=\"/ru/\">ru</a>"));

        IReadOnlyList<string> problems = site.Verify();

        string problem = Assert.Single(problems);
        Assert.Contains("ru/index.html", problem, StringComparison.Ordinal);
        Assert.Contains("/ru/", problem, StringComparison.Ordinal);
        Assert.Contains("outside the site path", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void FindsAnEntityEncodedEscapingUrl()
    {
        using SiteDir site = SiteDir.Valid();
        site.Write("en/index.html", TestPages.Html("<a href=\"&#47;ru/\">ru</a>"));

        IReadOnlyList<string> problems = site.Verify();

        string problem = Assert.Single(problems);
        Assert.Contains("en/index.html", problem, StringComparison.Ordinal);
        Assert.Contains("/ru/", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void FindsACssUrlAndAnImportMapEntryOutsideThePrefix()
    {
        using SiteDir site = SiteDir.Valid();
        site.Write("css/site.abcdefghij.css", "@font-face{src:url(/fonts/x.woff2)}");
        site.Write("en/index.html", TestPages.Html("<p>x</p>", "<script type=\"importmap\">{\"imports\":{\"x\":\"/x.js\"}}</script>"));
        site.Write("index.html", TestPages.Html("<link rel=\"stylesheet\" href=\"/dit/css/site.abcdefghij.css\">"));

        IReadOnlyList<string> problems = site.Verify();

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.StartsWith("css/site.abcdefghij.css:", StringComparison.Ordinal) && p.Contains("/fonts/x.woff2", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("en/index.html:", StringComparison.Ordinal) && p.Contains("/x.js", StringComparison.Ordinal) && p.Contains("import map", StringComparison.Ordinal));
    }

    [Fact]
    public void ImportMapIntegrityValuesAreHashesAndItsKeysAreCheckedAsUrls()
    {
        // A published build's import map pins every runtime file: {"integrity": {"<url>": "sha256-<hash>"}}. The value is a hash.
        using SiteDir site = SiteDir.Valid();
        string Hash = Sha256Of("console.log(1);");
        site.Write(
            "en/index.html",
            TestPages.Html("<p>x</p>", "<script type=\"importmap\">{\"imports\":{\"app\":\"/dit/js/app.abcdefghij.js\"},\"integrity\":{\"/dit/js/app.abcdefghij.js\":\"" + Hash + "\"}}</script>"));

        Assert.Empty(site.Verify());

        // The key is a URL, and a file that is not in the site is still found.
        site.Write(
            "en/index.html",
            TestPages.Html("<p>x</p>", "<script type=\"importmap\">{\"integrity\":{\"/dit/js/gone.abcdefghij.js\":\"" + Hash + "\"}}</script>"));

        string problem = Assert.Single(site.Verify());
        Assert.StartsWith("en/index.html:", problem, StringComparison.Ordinal);
        Assert.Contains("gone.abcdefghij.js", problem, StringComparison.Ordinal);
        Assert.Contains("integrity", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvesRelativeUrlsAgainstTheBaseElement()
    {
        using SiteDir site = SiteDir.Valid();
        site.Write(
            "en/index.html",
            TestPages.Html(
                "<a href=\"en/\">ok</a><script src=\"js/app.abcdefghij.js\"></script><a href=\"#main\">skip</a>",
                "<base href=\"/dit/\">"));

        IReadOnlyList<string> problems = site.Verify();

        string problem = Assert.Single(problems);
        Assert.Contains("#main", problem, StringComparison.Ordinal);
        Assert.Contains("<base>", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void FindsAMissingFileAndAMissingCspMetaOn404AndRedirectPages()
    {
        using SiteDir site = SiteDir.Valid();
        site.Write("en/index.html", TestPages.Html("<a href=\"/dit/en/gone.html\">gone</a>"));
        site.Write("404.html", TestPages.Html("<p>no</p>", cspFirst: false));
        site.Write("old/index.html", RedirectPage.Html("https://example.test/dit/en/", TestPages.Csp, TestPages.BuildId).Replace("<meta http-equiv=\"Content-Security-Policy\"", "<meta name=\"x\"", StringComparison.Ordinal));

        IReadOnlyList<string> problems = site.Verify();

        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.StartsWith("en/index.html:", StringComparison.Ordinal) && p.Contains("gone.html", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("404.html:", StringComparison.Ordinal) && p.Contains("Content-Security-Policy", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("old/index.html:", StringComparison.Ordinal) && p.Contains("Content-Security-Policy", StringComparison.Ordinal));
    }

    [Fact]
    public void FindsAPageWithoutTheBuildIdOrWithAnotherOne()
    {
        using SiteDir site = SiteDir.Valid();
        site.Write("en/index.html", TestPages.Html("<p>x</p>", buildId: null));
        site.Write("404.html", TestPages.Html("<p>x</p>", buildId: "other.1.1"));

        IReadOnlyList<string> problems = site.Verify();

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.StartsWith("en/index.html:", StringComparison.Ordinal) && p.Contains("dit-build", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("404.html:", StringComparison.Ordinal) && p.Contains("other.1.1", StringComparison.Ordinal));
    }

    [Fact]
    public void FindsAnAppPageWithoutTheRecoveryScriptButNotARedirectPage()
    {
        using SiteDir site = SiteDir.Valid();
        site.Write("en/index.html", TestPages.Html("<p>x</p>", recovery: false));

        IReadOnlyList<string> problems = site.Verify();

        string problem = Assert.Single(problems);
        Assert.StartsWith("en/index.html:", problem, StringComparison.Ordinal);
        Assert.Contains("recovery script", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void FindsARecoveryScriptThatThePolicyDoesNotAllow()
    {
        using SiteDir site = SiteDir.Valid();
        site.Write("en/index.html", TestPages.Html("<p>x</p>", csp: "default-src 'self'"));

        IReadOnlyList<string> problems = site.Verify();

        string problem = Assert.Single(problems);
        Assert.Contains("hash", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void EligiblePatternMatchesOnlyImmutableFiles()
    {
        using SiteDir site = SiteDir.Valid();
        // "javascript" is ten characters of [a-z0-9]: the worker would treat this mutable file as content-addressed.
        site.Write("js/x.javascript.js", "mutable();");
        site.Write("en/guide.abcdefghij.html", TestPages.Html("<p>x</p>"));

        IReadOnlyList<string> problems = site.Verify();

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.StartsWith("js/x.javascript.js:", StringComparison.Ordinal) && p.Contains("immutable.json", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("en/guide.abcdefghij.html:", StringComparison.Ordinal));
    }

    [Fact]
    public void AFileMatchingTheRuleIsFineWhenListedOrRetained()
    {
        using SiteDir site = SiteDir.Valid();
        site.Write("js/old.abcdefghij.js", "old();");
        site.Write("data/en/q/q1.0123456789abcdef.json", "{}");
        site.WriteManifests(
            ["css/site.abcdefghij.css", "fonts/x.abcdefghij.woff2", "img/logo.0123456789.png", "js/app.abcdefghij.js", "data/en/q/q1.0123456789abcdef.json"],
            retained: ["js/old.abcdefghij.js"]);

        IReadOnlyList<string> problems = site.Verify();

        Assert.True(problems.Count == 0, ProblemsText(problems));
    }

    [Fact]
    public void FindsAnImmutableFileThatIsNotInTheSiteAndAMissingManifest()
    {
        using SiteDir site = SiteDir.Valid();
        site.WriteManifests(["css/site.abcdefghij.css", "fonts/x.abcdefghij.woff2", "img/logo.0123456789.png", "js/app.abcdefghij.js", "js/gone.abcdefghij.js"]);
        File.Delete(Path.Combine(site.Root, "build.json"));

        IReadOnlyList<string> problems = site.Verify();

        Assert.Contains(problems, p => p.StartsWith("build.json:", StringComparison.Ordinal) && p.Contains("missing", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("immutable.json:", StringComparison.Ordinal) && p.Contains("js/gone.abcdefghij.js", StringComparison.Ordinal));
    }

    [Fact]
    public void RetainedFilesAreOtherwiseIgnored()
    {
        using SiteDir site = SiteDir.Valid();
        // An old runtime file with a root-relative URL inside would be a problem if it were part of the new build.
        site.Write("css/old.abcdefghij.css", "a{background:url(/gone.png)}");
        site.Write("js/old.abcdefghij.js", "import('/gone.js');");
        site.WriteManifests(["css/site.abcdefghij.css", "fonts/x.abcdefghij.woff2", "img/logo.0123456789.png", "js/app.abcdefghij.js"], retained: ["css/old.abcdefghij.css", "js/old.abcdefghij.js"]);

        IReadOnlyList<string> problems = site.Verify();

        Assert.True(problems.Count == 0, ProblemsText(problems));
    }

    [Fact]
    public void FindsAnExternalUrlThatIsNotAllowed()
    {
        using SiteDir site = SiteDir.Valid();
        site.Write(
            "en/index.html",
            TestPages.Html(
                "<a href=\"https://tracker.example/x\">plain link</a><a href=\"https://github.com/o/r/issues/new?title=x\" rel=\"noopener noreferrer\">report</a>"
                + "<a href=\"https://abc.goatcounter.com/count\">gc</a>",
                "<script src=\"https://cdn.example/lib.js\"></script><img src=\"https://img.example/a.png\"><link rel=\"stylesheet\" href=\"//cdn.example/a.css\">"));

        IReadOnlyList<string> problems = site.Verify();

        Assert.Equal(4, problems.Count);
        Assert.Contains(problems, p => p.Contains("tracker.example", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("script[src]", StringComparison.Ordinal) && p.Contains("cdn.example", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("img.example", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("link[href]", StringComparison.Ordinal));
    }

    [Fact]
    public void FindsASrcsetOgImageAndRefreshOutsideThePrefix()
    {
        using SiteDir site = SiteDir.Valid();
        site.Write(
            "en/index.html",
            TestPages.Html(
                "<img srcset=\"/dit/img/logo.0123456789.png 1x, /img/big.png 2x\">",
                "<meta property=\"og:image\" content=\"https://example.test/og.png\"><meta property=\"og:title\" content=\"/not a url\">"
                + "<meta http-equiv=\"refresh\" content=\"5; url=/elsewhere/\">"));

        IReadOnlyList<string> problems = site.Verify();

        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.Contains("/img/big.png", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("og:image", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("meta refresh", StringComparison.Ordinal));
    }

    [Fact]
    public void FindsADirectoryLinkWithoutATrailingSlash()
    {
        using SiteDir site = SiteDir.Valid();
        site.Write("index.html", TestPages.Html("<a href=\"/dit/en\">en</a>"));

        IReadOnlyList<string> problems = site.Verify();

        string problem = Assert.Single(problems);
        Assert.Contains("trailing slash", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void FindsASitemapUrlOutsideThePrefixOrMissingAndAnInvalidXml()
    {
        using SiteDir site = SiteDir.Valid();
        site.Write(
            "sitemap.xml",
            "<?xml version=\"1.0\"?><urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\" xmlns:xhtml=\"http://www.w3.org/1999/xhtml\">"
            + "<url><loc>https://example.test/ru/</loc><xhtml:link rel=\"alternate\" hreflang=\"en\" href=\"https://example.test/dit/nowhere/\"/></url>"
            + "<url><loc>/relative/</loc></url></urlset>");
        site.Write("sitemap-en.xml", "<urlset><url></urlset>");

        IReadOnlyList<string> problems = site.Verify();

        Assert.Equal(4, problems.Count);
        Assert.Contains(problems, p => p.StartsWith("sitemap.xml:", StringComparison.Ordinal) && p.Contains("https://example.test/ru/", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("sitemap.xml:", StringComparison.Ordinal) && p.Contains("nowhere", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("sitemap.xml:", StringComparison.Ordinal) && p.Contains("absolute", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("sitemap-en.xml:", StringComparison.Ordinal) && p.Contains("XML", StringComparison.Ordinal));
    }

    [Fact]
    public void FindsAUrlInThePersistedComponentState()
    {
        using SiteDir site = SiteDir.Valid();
        string inner = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"link\":\"/ru/dotnet/\",\"index\":\"data/en/dotnet/index.0123456789abcdef.json\",\"ok\":\"/dit/en/\"}"));
        string state = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"key\":\"" + inner + "\"}"));
        site.Write("en/index.html", TestPages.Html($"<!--Blazor-WebAssembly-Component-State:{state}--><p>x</p>"));

        IReadOnlyList<string> problems = site.Verify();

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Contains("persisted state", StringComparison.Ordinal) && p.Contains("/ru/dotnet/", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("persisted state", StringComparison.Ordinal) && p.Contains("index.0123456789abcdef.json", StringComparison.Ordinal));
    }

    [Fact]
    public void FindsAUrlInTheIslandMarkerParameters()
    {
        using SiteDir site = SiteDir.Valid();
        string parameters = Convert.ToBase64String(Encoding.UTF8.GetBytes("[{\"baseHref\":\"/ru/\"}]"));
        site.Write("en/index.html", TestPages.Html("<!--Blazor:{\"type\":\"webassembly\",\"parameterValues\":\"" + parameters + "\"}--><p>x</p>"));

        IReadOnlyList<string> problems = site.Verify();

        string problem = Assert.Single(problems);
        Assert.Contains("persisted state", problem, StringComparison.Ordinal);
        Assert.Contains("/ru/", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void FindsAJsonLdUrlOnTheSiteHostOutsideThePrefix()
    {
        using SiteDir site = SiteDir.Valid();
        site.Write(
            "en/index.html",
            TestPages.Html(
                "<p>x</p>",
                "<script type=\"application/ld+json\">{\"@context\":\"https://schema.org\",\"url\":\"https://example.test/dit/en/\",\"sameAs\":\"https://example.org/p\",\"image\":\"https://example.test/og.png\"}</script>"));

        IReadOnlyList<string> problems = site.Verify();

        string problem = Assert.Single(problems);
        Assert.Contains("JSON-LD", problem, StringComparison.Ordinal);
        Assert.Contains("og.png", problem, StringComparison.Ordinal);
    }

    private const string GoogleMeta = "<meta name=\"google-site-verification\" content=\"g-token\">";
    private const string YandexMeta = "<meta name=\"yandex-verification\" content=\"y-token\">";

    private static readonly string[] ValidImmutable = ["css/site.abcdefghij.css", "fonts/x.abcdefghij.woff2", "img/logo.0123456789.png", "js/app.abcdefghij.js"];

    [Fact]
    public void AcceptsTheVerificationTagsOnTheFrontPagesWhenConfigured()
    {
        using SiteDir site = SiteDir.Valid();
        site.Write("index.html", TestPages.Html("<a href=\"/dit/en/\">en</a>", GoogleMeta + YandexMeta));
        site.Write("en/index.html", TestPages.Html("<p>home</p>", GoogleMeta + YandexMeta));
        site.WriteManifests(ValidImmutable, google: "g-token", yandex: "y-token");

        IReadOnlyList<string> problems = site.Verify();

        Assert.True(problems.Count == 0, ProblemsText(problems));
    }

    [Fact]
    public void FindsAMissingOrWrongVerificationTagOnAFrontPage()
    {
        using SiteDir site = SiteDir.Valid();
        site.Write("index.html", TestPages.Html("<a href=\"/dit/en/\">en</a>", GoogleMeta));
        site.Write("en/index.html", TestPages.Html("<p>home</p>", GoogleMeta.Replace("g-token", "other", StringComparison.Ordinal) + YandexMeta));
        site.WriteManifests(ValidImmutable, google: "g-token", yandex: "y-token");

        IReadOnlyList<string> problems = site.Verify();

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.StartsWith("index.html:", StringComparison.Ordinal) && p.Contains("yandex-verification", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("en/index.html:", StringComparison.Ordinal) && p.Contains("google-site-verification", StringComparison.Ordinal));
    }

    [Fact]
    public void FindsAVerificationTagOnAPageThatIsNotFrontOrWhenNoneIsConfigured()
    {
        using SiteDir configured = SiteDir.Valid();
        configured.Write("index.html", TestPages.Html("<a href=\"/dit/en/\">en</a>", GoogleMeta));
        configured.Write("404.html", TestPages.Html("<p>no</p>", GoogleMeta));
        configured.Write("en/index.html", TestPages.Html("<p>home</p>", GoogleMeta));
        configured.WriteManifests(ValidImmutable, google: "g-token");
        using SiteDir none = SiteDir.Valid();
        none.Write("en/index.html", TestPages.Html("<p>home</p>", YandexMeta));

        string problem = Assert.Single(configured.Verify());
        Assert.StartsWith("404.html:", problem, StringComparison.Ordinal);
        string unwanted = Assert.Single(none.Verify());
        Assert.StartsWith("en/index.html:", unwanted, StringComparison.Ordinal);
        Assert.Contains("none is configured", unwanted, StringComparison.Ordinal);
    }

    private static string Sha256Of(string text) => Sha256Of(Encoding.UTF8.GetBytes(text));

    private static string Sha256Of(byte[] bytes) => "sha256-" + Convert.ToBase64String(SHA256.HashData(bytes));

    private static string ImportMapPage(string integrityEntries) =>
        TestPages.Html("<p>x</p>", "<script type=\"importmap\">{\"integrity\":{" + integrityEntries + "}}</script>");

    [Theory]
    [InlineData("nonsense")]
    [InlineData("sha256-")]
    [InlineData("md5-AAAA")]
    [InlineData("sha256-AAAA")]
    [InlineData("")]
    public void ImportMapIntegrityValueMustBeAWellFormedHash(string value)
    {
        using SiteDir site = SiteDir.Valid();
        site.Write("en/index.html", ImportMapPage("\"/dit/js/app.abcdefghij.js\":\"" + value + "\""));

        string problem = Assert.Single(site.Verify());

        Assert.StartsWith("en/index.html:", problem, StringComparison.Ordinal);
        Assert.Contains("integrity", problem, StringComparison.Ordinal);
        Assert.Contains("well-formed", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportMapIntegrityMustMatchTheFileItNames()
    {
        using SiteDir site = SiteDir.Valid();
        site.Write("en/index.html", ImportMapPage("\"/dit/js/app.abcdefghij.js\":\"" + Sha256Of("other bytes") + "\""));

        string problem = Assert.Single(site.Verify());

        Assert.StartsWith("en/index.html:", problem, StringComparison.Ordinal);
        Assert.Contains("js/app.abcdefghij.js", problem, StringComparison.Ordinal);
        Assert.Contains("does not match", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportMapKeyNeedNotBeAFileButMustStayUnderThePrefix()
    {
        using SiteDir site = SiteDir.Valid();
        // The key is the specifier a script imports; only its target is fetched. The boot integrity table is imported by a plain name.
        site.Write("en/index.html", TestPages.Html("<p>x</p>", "<script type=\"importmap\">{\"imports\":{\"/dit/js/plain.mjs\":\"/dit/js/app.abcdefghij.js\"}}</script>"));
        Assert.Empty(site.Verify());

        site.Write("en/index.html", TestPages.Html("<p>x</p>", "<script type=\"importmap\">{\"imports\":{\"/js/plain.mjs\":\"/dit/js/app.abcdefghij.js\"}}</script>"));
        string problem = Assert.Single(site.Verify());
        Assert.Contains("outside the site path", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(384)]
    [InlineData(512)]
    public void ImportMapIntegrityAcceptsTheOtherSriAlgorithms(int bits)
    {
        using SiteDir site = SiteDir.Valid();
        byte[] bytes = Encoding.UTF8.GetBytes("console.log(1);");
        byte[] hash = bits == 384 ? SHA384.HashData(bytes) : SHA512.HashData(bytes);
        site.Write("en/index.html", ImportMapPage("\"/dit/js/app.abcdefghij.js\":\"sha" + bits + "-" + Convert.ToBase64String(hash) + "\""));

        Assert.Empty(site.Verify());
    }

    // The loader decodes the published .br files and verifies them against js/boot-integrity.{hash}.mjs (ADR 0027): a table
    // that is missing, stale or wrong makes every client fall back silently to the uncompressed file.
    private sealed class RuntimeSite : IDisposable
    {
        public const string Wasm = "_framework/Trainer.Web.Client.abcdefghij.wasm";

        public RuntimeSite(
            Func<byte[], byte[]>? compress = null,
            Func<string, string>? tableEntry = null,
            bool table = true,
            string? tableText = null,
            bool tableRouteMatchesBytes = true,
            bool brFile = true,
            bool wasmFile = true)
        {
            Site = SiteDir.Valid();
            WasmBytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("wasm bytes ", 300)));
            List<string> immutable = ["css/site.abcdefghij.css", "fonts/x.abcdefghij.woff2", "img/logo.0123456789.png", "js/app.abcdefghij.js"];
            if (wasmFile)
            {
                Site.WriteBytes(Wasm, WasmBytes);
                immutable.Add(Wasm);
            }

            if (brFile)
            {
                Site.WriteBytes(Wasm + ".br", (compress ?? Compress)(WasmBytes));
                immutable.Add(Wasm + ".br");
            }

            if (table)
            {
                string integrity = tableEntry is null ? Sha256Of(WasmBytes) : tableEntry(Wasm);
                string text = tableText ?? "export default {\"" + Wasm["_framework/".Length..] + "\":\"" + integrity + "\"};\n";
                string fingerprint = tableRouteMatchesBytes ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..10] : "0000000000";
                TableRoute = "js/boot-integrity." + fingerprint + ".mjs";
                Site.Write(TableRoute, text);
                TableText = text;
                immutable.Add(TableRoute);
            }

            Site.WriteManifests([.. immutable]);
        }

        public SiteDir Site { get; }

        public byte[] WasmBytes { get; }

        public string TableRoute { get; } = string.Empty;

        public string TableText { get; } = string.Empty;

        public IReadOnlyList<string> Verify() => Site.Verify();

        public void Dispose() => Site.Dispose();

        public static byte[] Compress(byte[] bytes)
        {
            using MemoryStream output = new();
            using (BrotliStream brotli = new(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                brotli.Write(bytes);
            }

            return output.ToArray();
        }
    }

    [Fact]
    public void AcceptsABrotliRuntimeWhoseTableMatchesTheFiles()
    {
        using RuntimeSite site = new();

        Assert.Empty(site.Verify());
    }

    [Fact]
    public void FindsABrotliRuntimeWithoutAnIntegrityTable()
    {
        using RuntimeSite site = new(table: false);

        string problem = Assert.Single(site.Verify());

        Assert.Contains("boot-integrity", problem, StringComparison.Ordinal);
        Assert.Contains("no integrity table", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void FindsATableEntryThatDoesNotMatchTheFile()
    {
        using RuntimeSite site = new(tableEntry: _ => Sha256Of("stale bytes"));

        string problem = Assert.Single(site.Verify());

        Assert.StartsWith(site.TableRoute + ":", problem, StringComparison.Ordinal);
        Assert.Contains("Trainer.Web.Client.abcdefghij.wasm", problem, StringComparison.Ordinal);
        Assert.Contains("does not match", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void FindsAMalformedTableEntry()
    {
        using RuntimeSite site = new(tableEntry: _ => "sha256-AAAA");

        Assert.Contains(site.Verify(), p => p.StartsWith("js/boot-integrity.", StringComparison.Ordinal) && p.Contains("well-formed", StringComparison.Ordinal));
    }

    [Fact]
    public void FindsATableEntryForAFileThatIsNotInTheSite()
    {
        using RuntimeSite site = new(wasmFile: false, brFile: false);

        string problem = Assert.Single(site.Verify());

        Assert.StartsWith(site.TableRoute + ":", problem, StringComparison.Ordinal);
        Assert.Contains("is not in the site", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void FindsACompressedFileWhoseRuntimeFileHasNoTableEntry()
    {
        using RuntimeSite site = new(tableText: "export default {};\n");

        string problem = Assert.Single(site.Verify());

        Assert.Contains("Trainer.Web.Client.abcdefghij.wasm.br", problem, StringComparison.Ordinal);
        Assert.Contains("has no entry", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void FindsACompressedFileThatDecodesToOtherBytes()
    {
        using RuntimeSite site = new(compress: _ => RuntimeSite.Compress(Encoding.UTF8.GetBytes("some other file")));

        string problem = Assert.Single(site.Verify());

        Assert.StartsWith(RuntimeSite.Wasm + ".br:", problem, StringComparison.Ordinal);
        Assert.Contains("does not decode to", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void FindsACompressedFileThatIsNotABrotliStream()
    {
        using RuntimeSite site = new(compress: _ => Encoding.UTF8.GetBytes("this is not brotli"));

        string problem = Assert.Single(site.Verify());

        Assert.StartsWith(RuntimeSite.Wasm + ".br:", problem, StringComparison.Ordinal);
        Assert.Contains("is not a valid Brotli stream", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void FindsATableWhoseNameDoesNotPromiseItsBytes()
    {
        using RuntimeSite site = new(tableRouteMatchesBytes: false);

        string problem = Assert.Single(site.Verify());

        Assert.StartsWith(site.TableRoute + ":", problem, StringComparison.Ordinal);
        Assert.Contains("name does not match its bytes", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not a module")]
    [InlineData("export default [];\n")]
    [InlineData("export default {\"a\":1};\n")]
    public void FindsATableThatIsNotTheModuleTheLoaderImports(string text)
    {
        using RuntimeSite site = new(tableText: text);

        Assert.Contains(site.Verify(), p => p.StartsWith("js/boot-integrity.", StringComparison.Ordinal) && p.Contains("export default", StringComparison.Ordinal));
    }

    // A page that boots the Brotli runtime (islands-start.js with data-brotli) imports the table by its plain name, which only
    // the page's import map can turn into the table's file. Without the map the loader refuses every decoded file.
    private static string BrotliIslandPage(string importMap) =>
        TestPages.Html("<p>x</p>", importMap + "<script src=\"/dit/js/app.abcdefghij.js\" data-brotli=\"true\"></script>");

    private static string BootMap(RuntimeSite site, string? plainKey = "/dit/js/boot-integrity.mjs", string? target = null, string? integrity = null)
    {
        target ??= "/dit/" + site.TableRoute;
        List<string> sections = [];
        if (plainKey is not null)
        {
            sections.Add("\"imports\":{\"" + plainKey + "\":\"" + target + "\"}");
        }

        if (integrity is not null)
        {
            sections.Add("\"integrity\":{\"" + target + "\":\"" + integrity + "\"}");
        }

        return "<script type=\"importmap\">{" + string.Join(",", sections) + "}</script>";
    }

    [Fact]
    public void AcceptsABrotliIslandPageThatMapsTheIntegrityTable()
    {
        using RuntimeSite site = new();
        site.Site.Write("en/boot/index.html", BrotliIslandPage(BootMap(site, integrity: Sha256Of(site.TableText))));

        Assert.Empty(site.Verify());
    }

    [Fact]
    public void FindsABrotliIslandPageWithoutAnImportMap()
    {
        using RuntimeSite site = new();
        site.Site.Write("en/boot/index.html", BrotliIslandPage(string.Empty));

        string problem = Assert.Single(site.Verify());

        Assert.StartsWith("en/boot/index.html:", problem, StringComparison.Ordinal);
        Assert.Contains("boot-integrity.mjs", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void FindsABrotliIslandPageWhoseMapDoesNotNameTheTable()
    {
        using RuntimeSite site = new();
        site.Site.Write("en/boot/index.html", BrotliIslandPage(BootMap(site, plainKey: "/dit/js/other.mjs", integrity: Sha256Of(site.TableText))));

        string problem = Assert.Single(site.Verify());

        Assert.StartsWith("en/boot/index.html:", problem, StringComparison.Ordinal);
        Assert.Contains("boot-integrity.mjs", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void FindsABrotliIslandPageWhoseMapPointsAtSomethingOtherThanTheTable()
    {
        using RuntimeSite site = new();
        site.Site.Write("en/boot/index.html", BrotliIslandPage(BootMap(site, target: "/dit/js/app.abcdefghij.js", integrity: Sha256Of("console.log(1);"))));

        string problem = Assert.Single(site.Verify());

        Assert.StartsWith("en/boot/index.html:", problem, StringComparison.Ordinal);
        Assert.Contains("not the integrity table", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void FindsABrotliIslandPageWhoseMapCarriesNoIntegrityForTheTable()
    {
        using RuntimeSite site = new();
        site.Site.Write("en/boot/index.html", BrotliIslandPage(BootMap(site)));

        string problem = Assert.Single(site.Verify());

        Assert.StartsWith("en/boot/index.html:", problem, StringComparison.Ordinal);
        Assert.Contains("integrity", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ABrotliFlagThatIsNotTrueNeedsNoTableMap()
    {
        using RuntimeSite site = new();
        site.Site.Write("en/boot/index.html", TestPages.Html("<p>x</p>", "<script src=\"/dit/js/app.abcdefghij.js\" data-brotli=\"false\"></script>"));

        Assert.Empty(site.Verify());
    }

    [Fact]
    public void AMissingUncompressedFileMakesTheCompressedOneUnusable()
    {
        using RuntimeSite site = new(wasmFile: false);

        Assert.Contains(site.Verify(), p => p.StartsWith(RuntimeSite.Wasm + ".br:", StringComparison.Ordinal) && p.Contains("uncompressed", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsAMissingDirectory() =>
        Assert.Single(ExportVerifier.Verify(Path.Combine(Path.GetTempPath(), "dit-missing-" + Guid.NewGuid().ToString("N")), TestPages.Site));
}
