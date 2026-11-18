using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Trainer.StaticSite.Tests;

public sealed class SiteExporterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(int Code, string Error, string Out)> RunAsync(ExportFixture fixture, string outDir, params string[] extra)
    {
        StringWriter error = new();
        StringWriter output = new();
        ToolServices services = new()
        {
            CrawlHandler = () => fixture.Handler,
            Delay = (_, _) => Task.CompletedTask,
            Error = error,
            Out = output,
        };
        string[] args =
        [
            "export", "--app", ExportFixture.App.ToString(), "--site-url", TestPages.Site.ToString(), "--out", outDir,
            "--build-id", TestPages.BuildId, "--commit", "deadbeef", .. extra,
        ];
        int code = await Program.RunAsync(args, services, Ct);
        return (code, error.ToString(), output.ToString());
    }

    private static string NewOut() => Path.Combine(Path.GetTempPath(), "dit-export-" + Guid.NewGuid().ToString("N"));

    private static void Cleanup(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Export_WritesPagesAssetsContentManifestsAndVerifiesClean()
    {
        ExportFixture fixture = new();
        string dir = NewOut();
        try
        {
            (int code, string error, _) = await RunAsync(fixture, dir);

            Assert.True(code == 0, error);
            Assert.Equal(TestPages.Html("<a href=\"/dit/en/\">English</a>"), File.ReadAllText(Path.Combine(dir, "index.html")));
            Assert.True(File.Exists(Path.Combine(dir, "en", "dotnet", "practice", "index.html")));
            Assert.True(File.Exists(Path.Combine(dir, "css", "site.css")));
            Assert.Equal([0, 97, 115, 109], File.ReadAllBytes(Path.Combine(dir, "_framework", "a.abcdefghij.wasm")));
            Assert.True(File.Exists(Path.Combine(dir, fixture.IndexPath.Replace('/', Path.DirectorySeparatorChar))));
            Assert.True(File.Exists(Path.Combine(dir, fixture.QuestionPath.Replace('/', Path.DirectorySeparatorChar))));
            Assert.True(File.Exists(Path.Combine(dir, "og", "q1.abcdefghij.png")));
            Assert.Contains("Not found", File.ReadAllText(Path.Combine(dir, "404.html")), StringComparison.Ordinal);

            BuildInfo build = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(dir, "build.json")), ExportJsonContext.Default.BuildInfo)!;
            Assert.Equal(TestPages.BuildId, build.BuildId);
            Assert.Equal("deadbeef", build.Commit);
            ImmutableInfo immutable = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(dir, "immutable.json")), ExportJsonContext.Default.ImmutableInfo)!;
            Assert.Equal(TestPages.BuildId, immutable.BuildId);
            Assert.Equal(TestPages.EligiblePattern, immutable.EligiblePattern);
            // The immutable assets, the Open Graph image and every content file; not the unfingerprinted alias, a page or the worker.
            Assert.Equal(
                [
                    "_framework/a.abcdefghij.wasm",
                    "css/site.abcdefghij.css",
                    fixture.IndexPath,
                    fixture.QuestionPath,
                    "js/app.abcdefghij.js",
                    "og/q1.abcdefghij.png",
                ],
                immutable.Files);

            IReadOnlyList<string> problems = ExportVerifier.Verify(dir, TestPages.Site);
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task Export_RetriesBusyResponsesThenSucceeds()
    {
        ExportFixture fixture = new();
        fixture.Handler.Add("/dit/en/", count =>
        {
            if (count <= 2)
            {
                HttpResponseMessage busy = new(HttpStatusCode.ServiceUnavailable) { Content = new ByteArrayContent([]) };
                busy.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return busy;
            }

            return FakeApp.Bytes(HttpStatusCode.OK, Encoding.UTF8.GetBytes(TestPages.Html("<p>English</p>")), "text/html");
        });
        string dir = NewOut();
        try
        {
            (int code, string error, _) = await RunAsync(fixture, dir);

            Assert.True(code == 0, error);
            Assert.Contains("English", File.ReadAllText(Path.Combine(dir, "en", "index.html")), StringComparison.Ordinal);
            Assert.Equal(3, fixture.Handler.CountOf("/dit/en/"));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task Export_GivesUpAfterFiveRetriesOfABusyResponse()
    {
        ExportFixture fixture = new();
        fixture.Handler.Add("/dit/en/", _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new ByteArrayContent([]) });
        string dir = NewOut();
        try
        {
            (int code, string error, _) = await RunAsync(fixture, dir);

            Assert.Equal(1, code);
            Assert.Contains("/dit/en/", error, StringComparison.Ordinal);
            Assert.Contains("429", error, StringComparison.Ordinal);
            Assert.Equal(6, fixture.Handler.CountOf("/dit/en/"));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task Export_FailsOnAnUnexpectedStatusNamingTheUrl()
    {
        ExportFixture fixture = new();
        fixture.Handler.Add("/dit/en/", _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new ByteArrayContent([]) });
        string dir = NewOut();
        try
        {
            (int code, string error, _) = await RunAsync(fixture, dir);

            Assert.Equal(1, code);
            Assert.Contains("http://app.test:5080/dit/en/", error, StringComparison.Ordinal);
            Assert.Contains("500", error, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task Export_CapturesRedirectsWithoutFollowingThem()
    {
        ExportFixture fixture = new();
        string dir = NewOut();
        try
        {
            (int code, string error, _) = await RunAsync(fixture, dir);

            Assert.True(code == 0, error);
            string page = File.ReadAllText(Path.Combine(dir, "en", "dotnet", "questions", "old", "index.html"));
            Assert.Contains("0; url=/dit/en/dotnet/questions/new/", page, StringComparison.Ordinal);
            Assert.Contains("rel=\"canonical\" href=\"https://example.test/dit/en/dotnet/questions/new/\"", page, StringComparison.Ordinal);
            AngleSharp.Html.Dom.IHtmlDocument document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(page);
            Assert.Equal(TestPages.Csp, document.Head!.Children[0].GetAttribute("content"));
            Assert.Equal(TestPages.BuildId, document.QuerySelector("meta[name='dit-build']")!.GetAttribute("content"));
            Assert.Empty(document.QuerySelectorAll("script"));
            // The old URL was asked for once; the target only by its own entry in the manifest, never through the redirect.
            Assert.Equal(1, fixture.Handler.CountOf("/dit/en/dotnet/questions/old/"));
            Assert.Equal(1, fixture.Handler.CountOf("/dit/en/dotnet/questions/new/"));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Theory]
    [InlineData("https://evil.test/")]
    [InlineData("https://example.test/other/")]
    [InlineData("http://app.test:5080/other/")]
    [InlineData("https://example.test:8443/dit/en/")]
    public async Task Export_RejectsAnOffSiteRedirectTarget(string location)
    {
        ExportFixture fixture = new();
        fixture.Handler.AddStatus("/dit/en/dotnet/questions/old/", HttpStatusCode.MovedPermanently, location);
        string dir = NewOut();
        try
        {
            (int code, string error, _) = await RunAsync(fixture, dir);

            Assert.Equal(1, code);
            Assert.Contains(location, error, StringComparison.Ordinal);
            Assert.Contains("/dit/en/dotnet/questions/old/", error, StringComparison.Ordinal);
            Assert.DoesNotContain(fixture.Handler.Requests, uri => uri.Host == new Uri(location).Host && uri.Host != "app.test");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Found)]
    public async Task Export_RejectsARedirectEntryThatIsNotA301Or308(HttpStatusCode status)
    {
        ExportFixture fixture = new();
        fixture.Handler.AddStatus("/dit/en/dotnet/questions/old/", status, "/dit/en/");
        string dir = NewOut();
        try
        {
            (int code, string error, _) = await RunAsync(fixture, dir);

            Assert.Equal(1, code);
            Assert.Contains("/dit/en/dotnet/questions/old/", error, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task Export_Accepts308AsARedirect()
    {
        ExportFixture fixture = new();
        fixture.Handler.AddStatus("/dit/en/dotnet/questions/old/", HttpStatusCode.PermanentRedirect, "/dit/en/dotnet/questions/new/");
        string dir = NewOut();
        try
        {
            (int code, string error, _) = await RunAsync(fixture, dir);

            Assert.True(code == 0, error);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task Export_Accepts404OnlyForTheNotFoundPage()
    {
        ExportFixture fixture = new();
        string dir = NewOut();
        try
        {
            (int code, string error, _) = await RunAsync(fixture, dir);
            Assert.True(code == 0, error);
            Assert.True(File.Exists(Path.Combine(dir, "404.html")));

            // A page of the manifest that answers 404 is an error, even when the not-found page is fine.
            fixture.Handler.Add("/dit/en/", _ => FakeApp.Bytes(HttpStatusCode.NotFound, Encoding.UTF8.GetBytes(TestPages.Html("x")), "text/html"));
            string second = NewOut();
            try
            {
                (int failed, string message, _) = await RunAsync(fixture, second);
                Assert.Equal(1, failed);
                Assert.Contains("/dit/en/", message, StringComparison.Ordinal);
                Assert.Contains("404", message, StringComparison.Ordinal);
            }
            finally
            {
                Cleanup(second);
            }

            // And the not-found page answering 200 is an error too: a missing 404 would publish a page that is not an error page.
            fixture.Handler.Add("/dit/en/", TestPages.Html("<p>English</p>"));
            fixture.Handler.Add("/dit/not-found", TestPages.Html("<p>Not found</p>"));
            string third = NewOut();
            try
            {
                (int ok, string note, _) = await RunAsync(fixture, third);
                Assert.Equal(1, ok);
                Assert.Contains("/dit/not-found", note, StringComparison.Ordinal);
            }
            finally
            {
                Cleanup(third);
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task Export_FailsWhenTheNotFoundPagePolicyDiffersFromTheRedirectPagePolicy()
    {
        ExportFixture fixture = new();
        fixture.Handler.Add("/dit/not-found", _ => FakeApp.Bytes(HttpStatusCode.NotFound, Encoding.UTF8.GetBytes(TestPages.Html("x", csp: "default-src 'none'")), "text/html"));
        string dir = NewOut();
        try
        {
            (int code, string error, _) = await RunAsync(fixture, dir);

            Assert.Equal(1, code);
            Assert.Contains("RedirectPageCsp", error, StringComparison.Ordinal);
            Assert.Contains("/dit/not-found", error, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task Export_WorksWithATrailingSeparatorOnTheOutputDirectory()
    {
        ExportFixture fixture = new();
        string dir = NewOut();
        try
        {
            (int code, string error, _) = await RunAsync(fixture, dir + Path.DirectorySeparatorChar);

            Assert.True(code == 0, error);
            Assert.True(File.Exists(Path.Combine(dir, "index.html")));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task Export_WritesTheConfiguredVerificationValuesToBuildJson()
    {
        ExportFixture fixture = new();
        fixture.Serve(fixture.Manifest with { GoogleSiteVerification = "g-token", YandexVerification = "y-token" });
        string dir = NewOut();
        try
        {
            (int code, string error, _) = await RunAsync(fixture, dir);

            Assert.True(code == 0, error);
            BuildInfo build = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(dir, "build.json")), ExportJsonContext.Default.BuildInfo)!;
            Assert.Equal("g-token", build.GoogleSiteVerification);
            Assert.Equal("y-token", build.YandexVerification);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task Export_WithOutboundAccessBlocked_MapsPublicUrlsToTheApp()
    {
        ExportFixture fixture = new();
        HashSet<string> hosts = new(StringComparer.OrdinalIgnoreCase);
        using BlockingHandler blocked = new(fixture.Handler, ExportFixture.App, hosts);
        string dir = NewOut();
        try
        {
            StringWriter error = new();
            ToolServices services = new() { CrawlHandler = () => blocked, Delay = (_, _) => Task.CompletedTask, Error = error, Out = TextWriter.Null };
            int code = await Program.RunAsync(
                ["export", "--app", ExportFixture.App.ToString(), "--site-url", TestPages.Site.ToString(), "--out", dir, "--build-id", TestPages.BuildId, "--commit", "c"],
                services,
                Ct);

            Assert.True(code == 0, error.ToString());
            // The page names its card by the public URL; the card came from the app, and no request left the app's origin.
            Assert.Equal("app.test", Assert.Single(hosts));
            Assert.Contains(fixture.Handler.Requests, uri => uri.AbsolutePath == "/dit/og/q1.abcdefghij.png" && uri.Host == "app.test");
            Assert.DoesNotContain("app.test", File.ReadAllText(Path.Combine(dir, "en", "dotnet", "practice", "index.html")), StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Theory]
    [InlineData("https://example.test/dit/en/dotnet/questions/new/")]
    [InlineData("http://app.test:5080/dit/en/dotnet/questions/new/")]
    [InlineData("/dit/en/dotnet/questions/new/")]
    public async Task Export_AcceptsRedirectLocationOnPublicOrTransportOrigin(string location)
    {
        ExportFixture fixture = new();
        fixture.Handler.AddStatus("/dit/en/dotnet/questions/old/", HttpStatusCode.MovedPermanently, location);
        string dir = NewOut();
        try
        {
            (int code, string error, _) = await RunAsync(fixture, dir);

            Assert.True(code == 0, error);
            string page = File.ReadAllText(Path.Combine(dir, "en", "dotnet", "questions", "old", "index.html"));
            Assert.Contains("url=/dit/en/dotnet/questions/new/", page, StringComparison.Ordinal);
            Assert.DoesNotContain("app.test", page, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task Export_RejectsAnOpenGraphImageOutsideTheSite()
    {
        ExportFixture fixture = new();
        fixture.Handler.Add(
            "/dit/en/dotnet/practice/",
            TestPages.Html("<p>x</p>", "<meta property=\"og:image\" content=\"https://cdn.other/og.png\">"));
        string dir = NewOut();
        try
        {
            (int code, string error, _) = await RunAsync(fixture, dir);

            Assert.Equal(1, code);
            Assert.Contains("https://cdn.other/og.png", error, StringComparison.Ordinal);
            Assert.DoesNotContain(fixture.Handler.Requests, uri => uri.Host == "cdn.other");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task Export_SavesTheBytesTheAppServes_DecodingAnyTransportCompression()
    {
        ExportFixture fixture = new();
        byte[] question = Encoding.UTF8.GetBytes("{\"id\":\"q1\"}");
        fixture.Handler.Add("/dit/" + fixture.QuestionPath, _ =>
        {
            using MemoryStream compressed = new();
            using (GZipStream gzip = new(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                gzip.Write(question);
            }

            HttpResponseMessage response = FakeApp.Bytes(HttpStatusCode.OK, compressed.ToArray(), "application/json");
            response.Content.Headers.ContentEncoding.Add("gzip");
            return response;
        });
        string dir = NewOut();
        try
        {
            (int code, string error, _) = await RunAsync(fixture, dir);

            Assert.True(code == 0, error);
            byte[] saved = File.ReadAllBytes(Path.Combine(dir, fixture.QuestionPath.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Equal(question, saved);
            // The shard's name carries the hash of its bytes.
            Assert.Equal(fixture.QuestionHash, TestPages.Digest(saved));
            Assert.All(fixture.Handler.AcceptEncodings, value => Assert.Equal("identity", value));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Theory]
    [InlineData("a/../../b")]
    [InlineData("..")]
    [InlineData("q1?x=1")]
    public async Task Export_AMalformedQuestionIdInAnIndex_FailsCleanlyNamingTheIndex(string questionId)
    {
        ExportFixture fixture = new(questionId: questionId);
        string dir = NewOut();
        try
        {
            (int code, string error, _) = await RunAsync(fixture, dir);

            Assert.Equal(1, code);
            Assert.Contains(fixture.IndexPath, error, StringComparison.Ordinal);
            Assert.Contains("question id", error, StringComparison.Ordinal);
            Assert.DoesNotContain("Unhandled exception", error, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task Export_RejectsAShardWhoseBytesDoNotMatchItsName()
    {
        ExportFixture fixture = new();
        fixture.Handler.Add("/dit/" + fixture.QuestionPath, "{\"id\":\"tampered\"}", "application/json");
        string dir = NewOut();
        try
        {
            (int code, string error, _) = await RunAsync(fixture, dir);

            Assert.Equal(1, code);
            Assert.Contains(fixture.QuestionPath, error, StringComparison.Ordinal);
            Assert.Contains("hash", error, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task Export_BrotliRuntimeRoutesAreSavedRawAndImmutable()
    {
        ExportFixture fixture = new(brotli: true);
        // A server that labels the .br file as Brotli-encoded must not get its file decoded: the file is the Brotli stream.
        byte[] stream = ExportFixture.BrotliOf(ExportFixture.WasmBytes);
        fixture.Handler.Add("/dit/_framework/a.abcdefghij.wasm.br", _ =>
        {
            HttpResponseMessage response = FakeApp.Bytes(HttpStatusCode.OK, stream, "application/octet-stream");
            response.Content.Headers.ContentEncoding.Add("br");
            return response;
        });
        string dir = NewOut();
        try
        {
            (int code, string error, _) = await RunAsync(fixture, dir);

            Assert.True(code == 0, error);
            Assert.Equal(stream, File.ReadAllBytes(Path.Combine(dir, "_framework", "a.abcdefghij.wasm.br")));
            ImmutableInfo immutable = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(dir, "immutable.json")), ExportJsonContext.Default.ImmutableInfo)!;
            Assert.Contains("_framework/a.abcdefghij.wasm.br", immutable.Files);
            IReadOnlyList<string> problems = ExportVerifier.Verify(dir, TestPages.Site);
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task Export_IsIndependentOfResponseHeaders()
    {
        string first = NewOut();
        string second = NewOut();
        try
        {
            ExportFixture a = new();
            ExportFixture b = new();
            foreach (string path in new[] { "/dit/css/site.abcdefghij.css", "/dit/_framework/a.abcdefghij.wasm", "/dit/js/app.abcdefghij.js" })
            {
                FakeApp app = b.Handler;
                Func<int, HttpResponseMessage> inner = _ => FakeApp.Bytes(HttpStatusCode.OK, [1, 2, 3], "application/octet-stream");
                app.Add(path, count =>
                {
                    HttpResponseMessage response = inner(count);
                    response.Content.Headers.LastModified = DateTimeOffset.UtcNow.AddDays(-count);
                    response.Headers.ETag = new EntityTagHeaderValue("\"" + Guid.NewGuid().ToString("N") + "\"");
                    return response;
                });
                a.Handler.Add(path, _ => FakeApp.Bytes(HttpStatusCode.OK, [1, 2, 3], "application/octet-stream"));
            }

            Assert.Equal(0, (await RunAsync(a, first)).Code);
            Assert.Equal(0, (await RunAsync(b, second)).Code);

            // build.json carries the build time; everything else is a function of the bytes served.
            foreach (string file in Directory.EnumerateFiles(first, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("build.json", StringComparison.Ordinal)))
            {
                string other = Path.Combine(second, Path.GetRelativePath(first, file));
                Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(other));
            }
        }
        finally
        {
            Cleanup(first);
            Cleanup(second);
        }
    }

    [Fact]
    public async Task Export_KeepsAtMostFourRequestsInFlight()
    {
        ExportFixture fixture = new();
        fixture.Handler.Latency = TimeSpan.FromMilliseconds(20);
        string dir = NewOut();
        try
        {
            (int code, string error, _) = await RunAsync(fixture, dir);

            Assert.True(code == 0, error);
            Assert.InRange(fixture.Handler.MaxInFlight, 2, SiteExporter.MaxInFlight);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task Export_FailsWhenTheAppRunsAnotherBuildId()
    {
        ExportFixture fixture = new();
        string dir = NewOut();
        try
        {
            StringWriter error = new();
            ToolServices services = new() { CrawlHandler = () => fixture.Handler, Delay = (_, _) => Task.CompletedTask, Error = error, Out = TextWriter.Null };
            int code = await Program.RunAsync(
                ["export", "--app", ExportFixture.App.ToString(), "--site-url", TestPages.Site.ToString(), "--out", dir, "--build-id", "another.1.1", "--commit", "c"],
                services,
                Ct);

            Assert.Equal(1, code);
            Assert.Contains("App__StaticSite__BuildId=another.1.1", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task Export_RefusesAManifestEntryOutsideThePrefix()
    {
        ExportFixture fixture = new();
        fixture.Serve(fixture.Manifest with { Pages = [.. fixture.Manifest.Pages, "/elsewhere/"] });
        string dir = NewOut();
        try
        {
            (int code, string error, _) = await RunAsync(fixture, dir);

            Assert.Equal(1, code);
            Assert.Contains("/elsewhere/", error, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task Export_RefusesTraversalAndANonEmptyOutputDirectory()
    {
        ExportFixture fixture = new();
        fixture.Serve(fixture.Manifest with { Assets = [.. fixture.Manifest.Assets, "/dit/../escape.txt"] });
        string dir = NewOut();
        try
        {
            (int code, string error, _) = await RunAsync(fixture, dir);
            Assert.Equal(1, code);
            Assert.Contains("escape.txt", error, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(dir)!, "escape.txt")));

            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "leftover.txt"), "x");
            fixture.Serve(fixture.Manifest);
            (int again, string message, _) = await RunAsync(fixture, dir);
            Assert.Equal(1, again);
            Assert.Contains("not empty", message, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task Export_WorksForARootSite()
    {
        FakeApp app = new();
        string css = "a{}";
        ExportManifest manifest = new(
            Pages: ["/", "/ru/"],
            Redirects: [],
            Files: ["/robots.txt"],
            Indexes: [],
            Assets: ["/css/site.abcdefghij.css"],
            Immutable: ["/css/site.abcdefghij.css"],
            NotFoundPage: "/not-found",
            RedirectPageCsp: TestPages.Csp,
            BuildId: TestPages.BuildId,
            EligiblePattern: TestPages.EligiblePattern);
        app.Add("/static-export/manifest.json", JsonSerializer.Serialize(manifest, ExportJsonContext.Default.ExportManifest), "application/json");
        app.Add("/", TestPages.Html("<a href=\"/ru/\">ru</a>"));
        app.Add("/ru/", TestPages.Html("<a href=\"/\">home</a>"));
        app.Add("/robots.txt", "User-agent: *\n", "text/plain");
        app.Add("/css/site.abcdefghij.css", css, "text/css");
        app.Add("/not-found", _ => FakeApp.Bytes(HttpStatusCode.NotFound, Encoding.UTF8.GetBytes(TestPages.Html("none")), "text/html"));
        string dir = NewOut();
        try
        {
            StringWriter error = new();
            ToolServices services = new() { CrawlHandler = () => app, Delay = (_, _) => Task.CompletedTask, Error = error, Out = TextWriter.Null };
            int code = await Program.RunAsync(
                ["export", "--app", "http://app.test:5080/", "--site-url", "https://example.test/", "--out", dir, "--build-id", TestPages.BuildId, "--commit", "c"],
                services,
                Ct);

            Assert.True(code == 0, error.ToString());
            Assert.True(File.Exists(Path.Combine(dir, "ru", "index.html")));
            IReadOnlyList<string> problems = ExportVerifier.Verify(dir, new Uri("https://example.test/"));
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Theory]
    [InlineData("export", "--app", "http://x/")]
    [InlineData("export")]
    [InlineData("unknown")]
    [InlineData("export", "--app", "http://x/", "--site-url", "https://e/?x=1", "--out", "o", "--build-id", "b", "--commit", "c")]
    [InlineData("export", "--app", "http://u:p@x/", "--site-url", "https://e/", "--out", "o", "--build-id", "b", "--commit", "c")]
    [InlineData("verify", "--dir", "d", "--site-url", "https://e/#top")]
    [InlineData("verify", "--dir")]
    [InlineData("serve", "--dir", "d", "--site-url", "https://example.test/", "--port", "nope")]
    [InlineData("export", "--app", "ftp://x/", "--site-url", "https://e/", "--out", "o", "--build-id", "b", "--commit", "c")]
    [InlineData("export", "--app", "http://x/", "--site-url", "https://e/", "--out", "o", "--build-id", "b", "--commit", "c", "--live-build-id", "b")]
    [InlineData("export", "--app", "http://x/", "--site-url", "https://e/", "--out", "o", "--build-id", "b", "--commit", "c", "--retain-from", "https://e/")]
    public async Task Usage_Errors_Exit2(params string[] args)
    {
        StringWriter error = new();
        int code = await Program.RunAsync(args, new ToolServices { Error = error }, Ct);

        Assert.Equal(2, code);
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoArguments_Exit2()
    {
        StringWriter error = new();

        Assert.Equal(2, await Program.RunAsync([], new ToolServices { Error = error }, Ct));
    }

    [Fact]
    public async Task Verify_ExitCodesFollowTheProblemCount()
    {
        using SiteDir valid = SiteDir.Valid();
        StringWriter output = new();
        StringWriter error = new();
        ToolServices services = new() { Out = output, Error = error };

        Assert.Equal(0, await Program.RunAsync(["verify", "--dir", valid.Root, "--site-url", TestPages.Site.ToString()], services, Ct));

        valid.Write("en/index.html", TestPages.Html("<a href=\"/ru/\">x</a>"));
        Assert.Equal(1, await Program.RunAsync(["verify", "--dir", valid.Root, "--site-url", TestPages.Site.ToString()], services, Ct));
        Assert.Contains("en/index.html", error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Models a runner without outbound access: any request outside the app's origin throws instead of answering.</summary>
    private sealed class BlockingHandler(HttpMessageHandler inner, Uri allowed, HashSet<string> hosts) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (hosts)
            {
                hosts.Add(request.RequestUri!.Host);
            }

            if (!string.Equals(request.RequestUri!.Host, allowed.Host, StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpRequestException("No route to host " + request.RequestUri.Host);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
