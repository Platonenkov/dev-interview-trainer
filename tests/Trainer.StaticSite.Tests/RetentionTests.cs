using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;

namespace Trainer.StaticSite.Tests;

public sealed class RetentionTests
{
    private const string LiveBuild = "livebuild.5.1";
    private const string OldWasm = "_framework/old.abcdefghij.wasm";
    private const string OldCard = "og/old.abcdefghij.png";
    private static readonly string OldShard = "data/en/q/gone." + TestPages.Digest("{\"id\":\"gone\"}"u8) + ".json";
    private const string SharedCss = "css/site.abcdefghij.css";

    private static readonly byte[] SharedCssBytes = Encoding.UTF8.GetBytes("@font-face{src:url(\"../fonts/x.abcdefghij.woff2\")}");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string FullPath(Run run, string relative) => Path.Combine(run.Out, relative.Replace('/', Path.DirectorySeparatorChar));

    private sealed class Run
    {
        public Run(bool brotli = false)
        {
            Export = new ExportFixture(brotli);
            // Pages serves the previous deployment's files; the shared stylesheet has the bytes the new export also serves.
            Live.Add("/dit/build.json", BuildJson(LiveBuild), "application/json");
            Live.Add("/dit/immutable.json", ImmutableJson(LiveBuild, OldWasm, OldCard, OldShard, SharedCss), "application/json");
            Live.Add("/dit/" + OldWasm, [0, 97, 115, 109, 1], "application/wasm");
            Live.Add("/dit/" + OldCard, [137, 80, 78, 71, 9], "image/png");
            Live.Add("/dit/" + OldShard, "{\"id\":\"gone\"}", "application/json");
            Live.Add("/dit/" + SharedCss, SharedCssBytes, "text/css");
            Live.Add("/dit/en/dotnet/practice/", TestPages.Html("<p>previous practice</p>", buildId: LiveBuild));
            Export.Handler.Add("/dit/" + SharedCss, SharedCssBytes, "text/css");
            Export.Handler.Add("/dit/css/site.css", SharedCssBytes, "text/css");
        }

        public ExportFixture Export { get; }

        public FakeApp Live { get; } = new();

        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));

        public string Out { get; } = Path.Combine(Path.GetTempPath(), "dit-retain-" + Guid.NewGuid().ToString("N"));

        public string PreviousPage { get; } = Path.Combine(Path.GetTempPath(), "dit-previous-" + Guid.NewGuid().ToString("N") + ".html");

        public StringWriter Error { get; } = new();

        public static string BuildJson(string id) =>
            JsonSerializer.Serialize(new BuildInfo(id, "oldcommit", DateTimeOffset.UnixEpoch), ExportJsonContext.Default.BuildInfo);

        public static string ImmutableJson(string id, params string[] files) =>
            JsonSerializer.Serialize(new ImmutableInfo(id, files, TestPages.EligiblePattern), ExportJsonContext.Default.ImmutableInfo);

        public static string ImmutableJsonWithPattern(string id, string? pattern, params string[] files) =>
            JsonSerializer.Serialize(new ImmutableInfo(id, files, pattern), ExportJsonContext.Default.ImmutableInfo);

        public async Task<int> ExecuteAsync(string liveBuildId = LiveBuild, bool previousPage = false, bool trailingSeparator = false)
        {
            ToolServices services = new()
            {
                CrawlHandler = () => Export.Handler,
                RetentionHandler = () => Live,
                Time = Time,
                Delay = (span, _) =>
                {
                    Time.Advance(span);
                    return Task.CompletedTask;
                },
                Error = Error,
                Out = TextWriter.Null,
            };
            List<string> args =
            [
                "export", "--app", ExportFixture.App.ToString(), "--site-url", TestPages.Site.ToString(), "--out", trailingSeparator ? Out + Path.DirectorySeparatorChar : Out,
                "--build-id", TestPages.BuildId, "--commit", "c", "--retain-from", "https://live.test/dit/", "--live-build-id", liveBuildId,
            ];
            if (previousPage)
            {
                args.AddRange(["--previous-page-out", PreviousPage]);
            }

            return await Program.RunAsync([.. args], services, Ct);
        }

        public void Cleanup()
        {
            if (Directory.Exists(Out))
            {
                Directory.Delete(Out, recursive: true);
            }

            File.Delete(PreviousPage);
        }
    }

    [Fact]
    public async Task Retain_CopiesPreviousImmutableFilesIncludingOgImagesOnce()
    {
        Run run = new();
        try
        {
            int code = await run.ExecuteAsync();

            Assert.True(code == 0, run.Error.ToString());
            Assert.Equal([0, 97, 115, 109, 1], File.ReadAllBytes(FullPath(run, OldWasm)));
            Assert.Equal([137, 80, 78, 71, 9], File.ReadAllBytes(FullPath(run, OldCard)));
            Assert.Equal("{\"id\":\"gone\"}", File.ReadAllText(FullPath(run, OldShard)));
            // Every listed file is fetched once: the missing ones to copy them, the shared one to compare its bytes.
            foreach (string name in new[] { OldWasm, OldCard, OldShard, SharedCss })
            {
                Assert.Equal(1, run.Live.CountOf("/dit/" + name));
            }

            // The export's own copy of a shared name is kept as it is.
            Assert.Equal(SharedCssBytes, File.ReadAllBytes(FullPath(run, SharedCss)));

            // Retained files are in neither manifest of the new build, only in the record the verifier reads.
            ImmutableInfo immutable = JsonSerializer.Deserialize(File.ReadAllBytes(FullPath(run, "immutable.json")), ExportJsonContext.Default.ImmutableInfo)!;
            Assert.DoesNotContain(OldWasm, immutable.Files);
            Assert.DoesNotContain(OldCard, immutable.Files);
            Assert.DoesNotContain(OldShard, immutable.Files);
            Assert.Equal(TestPages.BuildId, immutable.BuildId);
            RetainedInfo retained = JsonSerializer.Deserialize(File.ReadAllBytes(FullPath(run, "retained.json")), ExportJsonContext.Default.RetainedInfo)!;
            Assert.Equal(LiveBuild, retained.BuildId);
            Assert.Equal([OldWasm, OldShard, OldCard], retained.Files);
            IReadOnlyList<string> problems = ExportVerifier.Verify(run.Out, TestPages.Site);
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));

            // The crawl only talked to the app; the live site only to retention.
            Assert.All(run.Export.Handler.Requests, uri => Assert.Equal("app.test", uri.Host));
            Assert.All(run.Live.Requests, uri => Assert.Equal("live.test", uri.Host));
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task Retain_ReadsBothManifestsWithACacheBustingQuery()
    {
        Run run = new();
        try
        {
            Assert.Equal(0, await run.ExecuteAsync());

            long now = run.Time.GetUtcNow().ToUnixTimeMilliseconds();
            Uri build = Assert.Single(run.Live.Requests, uri => uri.AbsolutePath == "/dit/build.json");
            Uri immutable = Assert.Single(run.Live.Requests, uri => uri.AbsolutePath == "/dit/immutable.json");
            Assert.Equal($"?_r={now}", build.Query);
            Assert.Equal($"?_r={now}", immutable.Query);
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task Retain_ManifestsWithDifferentBuildIds_RetryThenFail()
    {
        Run run = new();
        run.Live.Add("/dit/immutable.json", Run.ImmutableJson("stale.1.1", OldWasm), "application/json");
        DateTimeOffset start = run.Time.GetUtcNow();
        try
        {
            int code = await run.ExecuteAsync();

            Assert.Equal(1, code);
            string error = run.Error.ToString();
            Assert.Contains("stale.1.1", error, StringComparison.Ordinal);
            Assert.Contains(LiveBuild, error, StringComparison.Ordinal);
            Assert.True(run.Time.GetUtcNow() - start >= Retention.PropagationWindow, "retried for the whole window");
            Assert.True(run.Live.CountOf("/dit/immutable.json") > 3);
            Assert.False(File.Exists(FullPath(run, OldWasm)), "nothing is copied from an unsettled live site");
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task Retain_StaleManifestThatSettlesInTime_Succeeds()
    {
        Run run = new();
        run.Live.Add("/dit/immutable.json", count => FakeApp.Bytes(
            HttpStatusCode.OK,
            Encoding.UTF8.GetBytes(Run.ImmutableJson(count < 4 ? "stale.1.1" : LiveBuild, OldWasm)),
            "application/json"));
        try
        {
            int code = await run.ExecuteAsync();

            Assert.True(code == 0, run.Error.ToString());
            Assert.Equal(4, run.Live.CountOf("/dit/immutable.json"));
            Assert.True(File.Exists(FullPath(run, OldWasm)));
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task Retain_ManifestOfAnotherBuildThanTheGateRead_Fails()
    {
        Run run = new();
        try
        {
            // Both manifests agree with each other (build X) but the gate read build Y.
            int code = await run.ExecuteAsync(liveBuildId: "gate-read.9.9");

            Assert.Equal(1, code);
            string error = run.Error.ToString();
            Assert.Contains(LiveBuild, error, StringComparison.Ordinal);
            Assert.Contains("gate-read.9.9", error, StringComparison.Ordinal);
            Assert.False(File.Exists(FullPath(run, OldWasm)));
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task Retain_MissingLiveManifest_RetriesThenFails()
    {
        Run run = new();
        run.Live.AddStatus("/dit/build.json", HttpStatusCode.NotFound);
        try
        {
            Assert.Equal(1, await run.ExecuteAsync());
            Assert.Contains("404", run.Error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task Retain_PreviousPageOfAnotherBuild_RetryThenFail()
    {
        Run run = new();
        run.Live.Add("/dit/en/dotnet/practice/", TestPages.Html("<p>older</p>", buildId: "older.1.1"));
        DateTimeOffset start = run.Time.GetUtcNow();
        try
        {
            int code = await run.ExecuteAsync(previousPage: true);

            Assert.Equal(1, code);
            Assert.Contains("older.1.1", run.Error.ToString(), StringComparison.Ordinal);
            Assert.True(run.Time.GetUtcNow() - start >= Retention.PropagationWindow);
            Assert.False(File.Exists(run.PreviousPage));
            Assert.All(
                run.Live.Requests.Where(uri => uri.AbsolutePath == "/dit/en/dotnet/practice/"),
                uri => Assert.StartsWith("?_r=", uri.Query, StringComparison.Ordinal));
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task Retain_PreviousPageThatSettles_IsWrittenToTheFile()
    {
        Run run = new();
        string html = TestPages.Html("<p>previous practice</p>", buildId: LiveBuild);
        run.Live.Add("/dit/en/dotnet/practice/", count => FakeApp.Bytes(
            HttpStatusCode.OK,
            Encoding.UTF8.GetBytes(count < 3 ? TestPages.Html("<p>older</p>", buildId: "older.1.1") : html),
            "text/html"));
        try
        {
            int code = await run.ExecuteAsync(previousPage: true);

            Assert.True(code == 0, run.Error.ToString());
            Assert.Equal(html, File.ReadAllText(run.PreviousPage));
            Assert.Equal(3, run.Live.CountOf("/dit/en/dotnet/practice/"));
        }
        finally
        {
            run.Cleanup();
        }
    }

    private const string CompressedRuntime = "_framework/a.abcdefghij.wasm.br";

    private static byte[] BrotliWith(byte[] bytes, int quality, int window)
    {
        byte[] buffer = new byte[System.IO.Compression.BrotliEncoder.GetMaxCompressedLength(bytes.Length)];
        using System.IO.Compression.BrotliEncoder encoder = new(quality, window);
        OperationStatus status = encoder.Compress(bytes, buffer, out int consumed, out int written, isFinalBlock: true);
        Assert.Equal(OperationStatus.Done, status);
        Assert.Equal(bytes.Length, consumed);
        return buffer[..written];
    }

    [Fact]
    public async Task Retain_ACompressedFileWithOtherBytesButTheSameDecodedBytes_KeepsTheNewCopy()
    {
        // A compressor change alters the .br bytes while the wasm, and so the fingerprint in the name, stays: both copies
        // decode to one file, which is what the loader checks, so this must not block every later deployment.
        Run run = new(brotli: true);
        byte[] liveBytes = BrotliWith(ExportFixture.WasmBytes, quality: 5, window: 16);
        byte[] newBytes = ExportFixture.BrotliOf(ExportFixture.WasmBytes);
        Assert.NotEqual(newBytes, liveBytes);
        run.Live.Add("/dit/immutable.json", Run.ImmutableJson(LiveBuild, CompressedRuntime), "application/json");
        run.Live.Add("/dit/" + CompressedRuntime, liveBytes, "application/octet-stream");
        try
        {
            int code = await run.ExecuteAsync();

            Assert.True(code == 0, run.Error.ToString());
            Assert.Equal(newBytes, File.ReadAllBytes(FullPath(run, CompressedRuntime)));
            RetainedInfo retained = JsonSerializer.Deserialize(File.ReadAllBytes(FullPath(run, "retained.json")), ExportJsonContext.Default.RetainedInfo)!;
            Assert.Empty(retained.Files);
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task Retain_ACompressedFileWithDifferentDecodedBytes_StillFails()
    {
        Run run = new(brotli: true);
        run.Live.Add("/dit/immutable.json", Run.ImmutableJson(LiveBuild, CompressedRuntime), "application/json");
        run.Live.Add("/dit/" + CompressedRuntime, ExportFixture.BrotliOf([1, 2, 3, 4, 5]), "application/octet-stream");
        try
        {
            int code = await run.ExecuteAsync();

            Assert.Equal(1, code);
            string error = run.Error.ToString();
            Assert.Contains(CompressedRuntime, error, StringComparison.Ordinal);
            Assert.Contains("different bytes", error, StringComparison.Ordinal);
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task Retain_CompressedCopiesThatAgreeOnlyWithEachOther_FailAgainstThePlainSibling()
    {
        // Two copies that decode to the same bytes prove nothing when those bytes are not the plain file the loader verifies
        // them against (the case of two identically truncated streams, which a decoder may end without an error).
        Run run = new(brotli: true);
        byte[] other = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("not the wasm ", 300)));
        byte[] liveBytes = BrotliWith(other, quality: 5, window: 16);
        byte[] newBytes = ExportFixture.BrotliOf(other);
        Assert.NotEqual(newBytes, liveBytes);
        run.Export.Handler.Add("/dit/" + CompressedRuntime, newBytes, "application/octet-stream");
        run.Live.Add("/dit/immutable.json", Run.ImmutableJson(LiveBuild, CompressedRuntime), "application/json");
        run.Live.Add("/dit/" + CompressedRuntime, liveBytes, "application/octet-stream");
        try
        {
            int code = await run.ExecuteAsync();

            Assert.Equal(1, code);
            string error = run.Error.ToString();
            Assert.Contains(CompressedRuntime, error, StringComparison.Ordinal);
            Assert.Contains("different bytes", error, StringComparison.Ordinal);
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task Retain_ACompressedFileThatIsNotBrotli_Fails()
    {
        Run run = new(brotli: true);
        run.Live.Add("/dit/immutable.json", Run.ImmutableJson(LiveBuild, CompressedRuntime), "application/json");
        run.Live.Add("/dit/" + CompressedRuntime, "this is not a brotli stream, only text", "application/octet-stream");
        try
        {
            int code = await run.ExecuteAsync();

            Assert.Equal(1, code);
            string error = run.Error.ToString();
            Assert.Contains(CompressedRuntime, error, StringComparison.Ordinal);
            Assert.Contains("Brotli", error, StringComparison.Ordinal);
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task Retain_JudgesTheLiveNamesByTheLiveBuildsOwnPattern_NotByTheNewBuildsRule()
    {
        // The live build named its immutable files with a twelve-character fingerprint, a shape the new build's rule (exactly
        // ten) does not accept: the new rule must not make every later deployment fail.
        Run run = new();
        const string liveOnly = "_framework/older.abcdefghijkl.wasm";
        const string livePattern = """^(?:.+\.[a-z0-9]{12}\.[A-Za-z0-9.]+)$""";
        run.Live.Add("/dit/immutable.json", Run.ImmutableJsonWithPattern(LiveBuild, livePattern, liveOnly), "application/json");
        run.Live.Add("/dit/" + liveOnly, [0, 97, 115, 109, 2], "application/wasm");
        try
        {
            int code = await run.ExecuteAsync();

            Assert.True(code == 0, run.Error.ToString());
            Assert.Equal([0, 97, 115, 109, 2], File.ReadAllBytes(FullPath(run, liveOnly)));
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task Retain_ANameThatTheLiveBuildsOwnPatternDoesNotAcceptEither_IsRefused()
    {
        Run run = new();
        const string livePattern = """^(?:.+\.[a-z0-9]{12}\.[A-Za-z0-9.]+)$""";
        run.Live.Add("/dit/immutable.json", Run.ImmutableJsonWithPattern(LiveBuild, livePattern, "js/mutable.js"), "application/json");
        run.Live.Add("/dit/js/mutable.js", "console.log(1);", "text/javascript");
        try
        {
            int code = await run.ExecuteAsync();

            Assert.Equal(1, code);
            Assert.Contains("js/mutable.js", run.Error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task Retain_ALiveManifestWithoutAPattern_FallsBackToTheNewBuildsRule()
    {
        Run run = new();
        run.Live.Add("/dit/immutable.json", Run.ImmutableJsonWithPattern(LiveBuild, null, OldWasm, "js/mutable.js"), "application/json");
        try
        {
            int code = await run.ExecuteAsync();

            Assert.Equal(1, code);
            Assert.Contains("js/mutable.js", run.Error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task Retain_ALiveManifestWithAnInvalidPattern_FailsWithAMessage()
    {
        Run run = new();
        run.Live.Add("/dit/immutable.json", Run.ImmutableJsonWithPattern(LiveBuild, "(unclosed", OldWasm), "application/json");
        try
        {
            int code = await run.ExecuteAsync();

            Assert.Equal(1, code);
            Assert.Contains("eligibility pattern", run.Error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task RetainedNameWithDifferentBytes_Fails()
    {
        Run run = new();
        run.Live.Add("/dit/" + SharedCss, "different bytes under the same content-addressed name", "text/css");
        try
        {
            int code = await run.ExecuteAsync();

            Assert.Equal(1, code);
            string error = run.Error.ToString();
            Assert.Contains(SharedCss, error, StringComparison.Ordinal);
            Assert.Contains("different bytes", error, StringComparison.Ordinal);
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "text/html")]
    [InlineData(HttpStatusCode.OK, "text/html")]
    public async Task Retain_ListedFileThatIsGoneOrAnHtml404Body_Fails(HttpStatusCode status, string contentType)
    {
        Run run = new();
        run.Live.Add("/dit/" + OldWasm, _ => FakeApp.Bytes(status, Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>Not found</body></html>"), contentType));
        try
        {
            int code = await run.ExecuteAsync();

            Assert.Equal(1, code);
            Assert.Contains(OldWasm, run.Error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task Retain_HtmlBodyServedAsAnotherContentType_IsStillRejected()
    {
        Run run = new();
        run.Live.Add("/dit/" + OldWasm, "  <!doctype html><html></html>", "application/octet-stream");
        try
        {
            Assert.Equal(1, await run.ExecuteAsync());
            Assert.Contains("HTML", run.Error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Theory]
    [InlineData("../escape.abcdefghij.js")]
    [InlineData("/etc/passwd")]
    [InlineData("a/../../b.js")]
    [InlineData("css/")]
    public async Task Retain_RefusesAListedNameThatIsNotAPlainRelativeFile(string name)
    {
        Run run = new();
        run.Live.Add("/dit/immutable.json", Run.ImmutableJson(LiveBuild, name), "application/json");
        try
        {
            int code = await run.ExecuteAsync();

            Assert.Equal(1, code);
            Assert.Contains(name, run.Error.ToString(), StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(run.Out)!, "escape.abcdefghij.js")));
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task Retain_SharedOgImageWithChangedBytes_KeepsTheNewCopyWithoutFetching()
    {
        Run run = new();
        // A card is named after its text and a version, not its bytes: a renderer update changes bytes under the same name.
        const string card = "og/en/site/0123456789abcdef.png";
        run.Export.Handler.Add(
            "/dit/en/dotnet/practice/",
            TestPages.Html("<p>x</p>", "<meta property=\"og:image\" content=\"https://example.test/dit/" + card + "\">"));
        run.Export.Handler.Add("/dit/" + card, [137, 80, 78, 71], "image/png");
        run.Live.Add("/dit/immutable.json", Run.ImmutableJson(LiveBuild, card), "application/json");
        run.Live.Add("/dit/" + card, [1, 1, 1, 1], "image/png");
        try
        {
            int code = await run.ExecuteAsync();

            Assert.True(code == 0, run.Error.ToString());
            Assert.Equal([137, 80, 78, 71], File.ReadAllBytes(FullPath(run, card)));
            Assert.Equal(0, run.Live.CountOf("/dit/" + card));
            RetainedInfo retained = JsonSerializer.Deserialize(File.ReadAllBytes(FullPath(run, "retained.json")), ExportJsonContext.Default.RetainedInfo)!;
            Assert.Empty(retained.Files);
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task Retain_WorksWithATrailingSeparatorOnTheOutputDirectory()
    {
        Run run = new();
        try
        {
            int code = await run.ExecuteAsync(trailingSeparator: true);

            Assert.True(code == 0, run.Error.ToString());
            Assert.True(File.Exists(FullPath(run, OldWasm)));
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task Retain_RefusesARetainedShardWhoseBytesDoNotMatchItsName()
    {
        Run run = new();
        run.Live.Add("/dit/" + OldShard, "{\"id\":\"tampered\"}", "application/json");
        try
        {
            Assert.Equal(1, await run.ExecuteAsync());
            Assert.Contains(OldShard, run.Error.ToString(), StringComparison.Ordinal);
            Assert.Contains("hash", run.Error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Theory]
    [InlineData("index.html")]
    [InlineData("en/page.abcdefghij.html")]
    [InlineData("og/card.html")]
    [InlineData("data/x.html")]
    [InlineData("js/app.js")]
    [InlineData("retained.json")]
    [InlineData("sw.js")]
    public async Task Retain_RefusesANameThatIsNotAContentAddressedFileACardOrAShard(string name)
    {
        Run run = new();
        run.Live.Add("/dit/immutable.json", Run.ImmutableJson(LiveBuild, name), "application/json");
        run.Live.Add("/dit/" + name, "<html></html>", "application/octet-stream");
        try
        {
            Assert.Equal(1, await run.ExecuteAsync());
            Assert.Contains(name, run.Error.ToString(), StringComparison.Ordinal);
            Assert.Equal(0, run.Live.CountOf("/dit/" + name));
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public async Task Retain_DoesNotFollowARedirectToAnotherHost()
    {
        Run run = new();
        run.Live.AddStatus("/dit/" + OldWasm, HttpStatusCode.Found, "https://evil.test/old.wasm");
        try
        {
            Assert.Equal(1, await run.ExecuteAsync());
            Assert.Contains("302", run.Error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(run.Live.Requests, uri => uri.Host == "evil.test");
        }
        finally
        {
            run.Cleanup();
        }
    }

    [Fact]
    public void TheDefaultHttpHandlers_DoNotFollowRedirects()
    {
        ToolServices services = new();
        using HttpMessageHandler retention = services.RetentionHandler();
        using HttpMessageHandler crawl = services.CrawlHandler();

        Assert.False(((HttpClientHandler)retention).AllowAutoRedirect);
        Assert.False(((HttpClientHandler)crawl).AllowAutoRedirect);
    }

    [Fact]
    public async Task WithoutRetainFrom_NothingIsFetchedFromTheLiveSite()
    {
        ExportFixture export = new();
        FakeApp live = new();
        string dir = Path.Combine(Path.GetTempPath(), "dit-first-" + Guid.NewGuid().ToString("N"));
        try
        {
            ToolServices services = new() { CrawlHandler = () => export.Handler, RetentionHandler = () => live, Delay = (_, _) => Task.CompletedTask, Out = TextWriter.Null };
            int code = await Program.RunAsync(
                ["export", "--app", ExportFixture.App.ToString(), "--site-url", TestPages.Site.ToString(), "--out", dir, "--build-id", TestPages.BuildId, "--commit", "c"],
                services,
                Ct);

            Assert.Equal(0, code);
            Assert.Empty(live.Requests);
            Assert.False(File.Exists(Path.Combine(dir, "retained.json")));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
