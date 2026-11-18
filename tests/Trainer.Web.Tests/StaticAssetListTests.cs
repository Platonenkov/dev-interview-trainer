using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Trainer.Web.Startup;
using Trainer.Web.StaticSite;

namespace Trainer.Web.Tests;

public sealed class StaticAssetListTests
{
    private static readonly StaticAssetList Default = new(new ApplicationEnvironment("Trainer.Web"), Options.Create(new AppOptions()));

    [Fact]
    public void AssetList_ImmutableHasOnlyFingerprintedRoutes()
    {
        Assert.NotEmpty(Default.Immutable);
        Assert.All(Default.Immutable, route => Assert.Matches(@"\.[a-z0-9]{10}\.", route));
        // Every fingerprinted name is covered by the worker's rule, so the exporter can rely on one of them.
        Assert.All(Default.Immutable, route => Assert.True(StaticAssetRules.IsEligible(route), route));
        Assert.DoesNotContain("_framework/blazor.web.js", Default.Immutable);
        Assert.DoesNotContain("css/site.css", Default.Immutable);
        Assert.Contains(Default.Immutable, route => route.StartsWith("_framework/blazor.web.", StringComparison.Ordinal));
        Assert.Contains(Default.Immutable, route => route.StartsWith("css/site.", StringComparison.Ordinal));
        Assert.Contains(Default.Immutable, route => route.StartsWith("fonts/Inter-Regular.", StringComparison.Ordinal));
    }

    [Fact]
    public void AssetList_ImmutableIsASubsetOfTheExportedRoutes()
    {
        Assert.All(Default.Immutable, route => Assert.Contains(route, Default.ExportRoutes));
        Assert.Contains("_framework/blazor.web.js", Default.ExportRoutes);
    }

    [Fact]
    public void AssetList_ExportRoutesSkipPrecompressedVariants()
    {
        Assert.NotEmpty(Default.ExportRoutes);
        Assert.DoesNotContain(Default.ExportRoutes, route => route.EndsWith(".gz", StringComparison.Ordinal));
        Assert.DoesNotContain(Default.ExportRoutes, route => route.EndsWith(".br", StringComparison.Ordinal));
        Assert.Equal(Default.ExportRoutes.Count, Default.ExportRoutes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void AssetList_ExportsBrotliVariantsOfTheRuntime()
    {
        string name = "dit-brotli-" + Guid.NewGuid().ToString("N");
        string manifest = Path.Combine(AppContext.BaseDirectory, name + ".staticwebassets.endpoints.json");
        File.WriteAllText(manifest, "{\"Version\":1,\"ManifestType\":\"Build\",\"Endpoints\":[" + string.Join(',',
            Endpoint("_framework/a.abcdefghij.wasm", "_framework/a.wasm"),
            IntegrityOnly("_framework/a.abcdefghij.wasm.br"),
            IntegrityOnly("_framework/a.abcdefghij.wasm.gz"),
            Endpoint("_framework/dotnet.native.abcdefghij.wasm", "_framework/dotnet.native.wasm"),
            IntegrityOnly("_framework/dotnet.native.abcdefghij.wasm.br"),
            IntegrityOnly("_framework/dotnet.native.abcdefghij.wasm.gz"),
            Endpoint("js/b.abcdefghij.js", "js/b.js"),
            IntegrityOnly("js/b.abcdefghij.js.br")) + "]}");
        try
        {
            StaticAssetList brotli = new(new ApplicationEnvironment(name), Options.Create(new AppOptions { StaticSite = new StaticSiteOptions { BrotliRuntime = true } }));
            string[] wasm = [.. brotli.ExportRoutes.Where(r => r.StartsWith("_framework/", StringComparison.Ordinal) && r.EndsWith(".wasm", StringComparison.Ordinal))];

            Assert.Equal(2, wasm.Length);
            Assert.All(wasm, route =>
            {
                Assert.Contains(route + ".br", brotli.ExportRoutes);
                Assert.Contains(route + ".br", brotli.Immutable);
                Assert.True(StaticAssetRules.IsEligible(route + ".br"), route + ".br must match the worker's rule");
            });
            Assert.DoesNotContain(brotli.ExportRoutes, route => route.EndsWith(".gz", StringComparison.Ordinal));
            Assert.DoesNotContain(brotli.Immutable, route => route.EndsWith(".gz", StringComparison.Ordinal));
            Assert.DoesNotContain("js/b.abcdefghij.js.br", brotli.ExportRoutes);

            // The default (false) keeps Task 14's behaviour over the same manifest.
            StaticAssetList plain = new(new ApplicationEnvironment(name), Options.Create(new AppOptions()));
            Assert.DoesNotContain(plain.ExportRoutes, route => route.EndsWith(".br", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(manifest);
        }
    }

    [Fact]
    public void AssetList_BrotliRuntimeKeepsOnlyTheFrameworkBrRoutes()
    {
        string manifest = WriteManifest(
            Endpoint("_framework/a.abcdefghij.wasm", "_framework/a.wasm"),
            IntegrityOnly("_framework/a.abcdefghij.wasm.br"),
            IntegrityOnly("_framework/a.abcdefghij.wasm.gz"),
            IntegrityOnly("_framework/a.wasm.br"),
            IntegrityOnly("js/b.abcdefghij.js.br"));
        try
        {
            Assert.Equal(
                ["_framework/a.abcdefghij.wasm"],
                StaticAssetList.FromManifest(manifest, keepBrotliRuntime: false).ExportRoutes);
            StaticAssetList brotli = StaticAssetList.FromManifest(manifest, keepBrotliRuntime: true);
            string table = brotli.BootIntegrity!.Route;
            Assert.Equal(["_framework/a.abcdefghij.wasm", "_framework/a.abcdefghij.wasm.br", "_framework/a.wasm.br", table], brotli.ExportRoutes);
            // The variant is immutable exactly when its base route is; the alias's variant is not.
            Assert.Equal(["_framework/a.abcdefghij.wasm", "_framework/a.abcdefghij.wasm.br", table], brotli.Immutable);
        }
        finally
        {
            File.Delete(manifest);
        }
    }

    [Fact]
    public void AssetList_BootIntegrityIsTheSha256OfEveryFingerprintedRuntimeFile_AndOnlyWithTheBrotliRuntime()
    {
        string path = WriteManifest(
            WithIntegrity("_framework/a.abcdefghij.wasm", "_framework/a.wasm", "sha256-aaa="),
            WithIntegrity("_framework/dotnet.native.abcdefghij.wasm", "_framework/dotnet.native.wasm", "sha256-nnn="),
            WithIntegrity("_framework/a.abcdefghij.pdb", "_framework/a.pdb", "sha256-ppp="),
            // The variant's integrity is the hash of the compressed bytes and must never be taken for the file's.
            IntegrityOnly("_framework/a.abcdefghij.wasm.br"),
            // The alias has no fingerprint, a script is verified by the import map, and another folder is not the runtime.
            "{\"Route\":\"_framework/a.wasm\",\"AssetFile\":\"_framework/a.abcdefghij.wasm\",\"Selectors\":[],\"EndpointProperties\":[{\"Name\":\"integrity\",\"Value\":\"sha256-alias=\"}]}",
            WithIntegrity("_framework/dotnet.runtime.abcdefghij.js", "_framework/dotnet.runtime.js", "sha256-js="),
            WithIntegrity("data/c.abcdefghij.wasm", "data/c.wasm", "sha256-other="));
        try
        {
            StaticAssetList brotli = StaticAssetList.FromManifest(path, keepBrotliRuntime: true);

            BootIntegrityModule table = Assert.IsType<BootIntegrityModule>(brotli.BootIntegrity);
            Assert.Equal(
                new Dictionary<string, string>
                {
                    ["a.abcdefghij.wasm"] = "sha256-aaa=",
                    ["a.abcdefghij.pdb"] = "sha256-ppp=",
                    ["dotnet.native.abcdefghij.wasm"] = "sha256-nnn=",
                },
                table.Entries);
            // The table is a published, content-addressed file like any other, so it is crawled and immutable.
            Assert.Contains(table.Route, brotli.ExportRoutes);
            Assert.Contains(table.Route, brotli.Immutable);
            Assert.True(StaticAssetRules.IsEligible(table.Route), table.Route);

            StaticAssetList plain = StaticAssetList.FromManifest(path, keepBrotliRuntime: false);
            Assert.Null(plain.BootIntegrity);
            Assert.DoesNotContain(plain.ExportRoutes, route => route.Contains("boot-integrity", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BootIntegrityModule_IsDeterministicSortedAndNamedByItsBytes()
    {
        Dictionary<string, string> entries = new(StringComparer.Ordinal) { ["b.abcdefghij.wasm"] = "sha256-b+/=", ["a.abcdefghij.wasm"] = "sha256-a=" };

        BootIntegrityModule first = BootIntegrityModule.Create(entries);
        BootIntegrityModule second = BootIntegrityModule.Create(entries.Reverse().ToDictionary(StringComparer.Ordinal));

        Assert.Equal(first.Text, second.Text);
        Assert.Equal("export default {\"a.abcdefghij.wasm\":\"sha256-a=\",\"b.abcdefghij.wasm\":\"sha256-b+/=\"};\n", first.Text);
        Assert.Equal("sha256-" + Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(first.Text))), first.Integrity);
        Assert.Matches(@"^js/boot-integrity\.[a-z0-9]{10}\.mjs$", first.Route);
        Assert.NotEqual(first.Route, BootIntegrityModule.Create(new Dictionary<string, string>(StringComparer.Ordinal) { ["a.abcdefghij.wasm"] = "sha256-x=" }).Route);
        Assert.True(StaticAssetRules.IsEligible(first.Route));
    }

    [Fact]
    public void AssetList_SkipsSelectorVariantsAndDuplicates()
    {
        string path = WriteManifest(
            "{\"Route\":\"js/a.abcdefghij.js\",\"AssetFile\":\"js/a.js.gz\",\"Selectors\":[{\"Name\":\"Content-Encoding\",\"Value\":\"gzip\",\"Quality\":\"1\"}],\"EndpointProperties\":[{\"Name\":\"fingerprint\",\"Value\":\"abcdefghij\"},{\"Name\":\"label\",\"Value\":\"js/a.js\"}]}",
            Endpoint("js/a.abcdefghij.js", "js/a.js"),
            Endpoint("js/a.abcdefghij.js", "js/a.js"),
            "{\"Route\":\"js/a.js\",\"AssetFile\":\"js/a.js\",\"Selectors\":[],\"EndpointProperties\":[]}");
        try
        {
            StaticAssetList list = StaticAssetList.FromManifest(path, keepBrotliRuntime: false);

            Assert.Equal(["js/a.abcdefghij.js", "js/a.js"], list.ExportRoutes);
            Assert.Equal(["js/a.abcdefghij.js"], list.Immutable);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string Endpoint(string route, string label) =>
        $"{{\"Route\":\"{route}\",\"AssetFile\":\"{label}\",\"Selectors\":[],\"EndpointProperties\":[{{\"Name\":\"fingerprint\",\"Value\":\"abcdefghij\"}},{{\"Name\":\"label\",\"Value\":\"{label}\"}}]}}";

    private static string WithIntegrity(string route, string label, string integrity) =>
        $"{{\"Route\":\"{route}\",\"AssetFile\":\"{route}\",\"Selectors\":[],\"EndpointProperties\":[{{\"Name\":\"fingerprint\",\"Value\":\"abcdefghij\"}},{{\"Name\":\"integrity\",\"Value\":\"{integrity}\"}},{{\"Name\":\"label\",\"Value\":\"{label}\"}}]}}";

    // The shape of a precompressed route in the real manifest: an integrity property and nothing else.
    private static string IntegrityOnly(string route) =>
        $"{{\"Route\":\"{route}\",\"AssetFile\":\"{route}\",\"Selectors\":[],\"EndpointProperties\":[{{\"Name\":\"integrity\",\"Value\":\"sha256-x\"}}]}}";

    private static string WriteManifest(params string[] endpoints)
    {
        string path = Path.Combine(Path.GetTempPath(), "dit-endpoints-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{\"Version\":1,\"ManifestType\":\"Build\",\"Endpoints\":[" + string.Join(',', endpoints) + "]}");
        return path;
    }

    private sealed class ApplicationEnvironment(string name) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = name;

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

        public string WebRootPath { get; set; } = string.Empty;

        public string EnvironmentName { get; set; } = "Testing";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
