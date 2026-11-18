using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Trainer.StaticSite.Tests;

public sealed class PagesEmulatorTests : IDisposable
{
    private static readonly Uri Site = new("https://example.test/dit/");

    private readonly SiteDir _site = new();

    private readonly HttpClient _client = new(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None });

    private static readonly string[] AllowedHeaders = ["Date", "Cache-Control", "Content-Type", "Content-Length"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public PagesEmulatorTests()
    {
        _site.Write("index.html", "<html>root</html>");
        _site.Write("ru/index.html", "<html>ru</html>");
        _site.Write("404.html", "<html>missing</html>");
        _site.Write("js/app.abcdefghij.js", new string('a', 4000));
        _site.WriteBytes("_framework/a.abcdefghij.wasm", Enumerable.Repeat((byte)7, 6000).ToArray());
        _site.WriteBytes("_framework/a.abcdefghij.wasm.br", Enumerable.Repeat((byte)3, 600).ToArray());
        _site.WriteBytes("og/card.abcdefghij.png", Enumerable.Repeat((byte)5, 600).ToArray());
        _site.Write("data/en/index.0123456789abcdef.json", "{\"a\":1}");
    }

    public void Dispose()
    {
        _client.Dispose();
        _site.Dispose();
    }

    private Task<PagesEmulator> StartAsync(bool gzip = true) =>
        PagesEmulator.StartAsync(new PagesEmulatorOptions(_site.Root, Site, Gzip: gzip));

    private async Task<HttpResponseMessage> GetAsync(PagesEmulator emulator, string path, bool gzip = false, HttpMethod? method = null)
    {
        using HttpRequestMessage request = new(method ?? HttpMethod.Get, new Uri(emulator.BaseAddress, path));
        if (gzip)
        {
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        }

        return await _client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task BaseAddress_IsUnderThePrefixOnAFreePort()
    {
        await using PagesEmulator emulator = await StartAsync();

        Assert.Equal("/dit/", emulator.BaseAddress.AbsolutePath);
        Assert.True(emulator.BaseAddress.Port > 0);
        Assert.Equal("127.0.0.1", emulator.BaseAddress.Host);
    }

    [Fact]
    public async Task Serves_FilesAndDirectoryIndexes_WithOnlyThePagesHeaders()
    {
        await using PagesEmulator emulator = await StartAsync();

        foreach (string path in new[] { "", "ru/", "js/app.abcdefghij.js", "data/en/index.0123456789abcdef.json" })
        {
            using HttpResponseMessage response = await GetAsync(emulator, path);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("max-age=600", response.Headers.CacheControl!.ToString());
            string[] names = [.. response.Headers.Select(h => h.Key).Concat(response.Content.Headers.Select(h => h.Key))];
            Assert.All(names, name => Assert.Contains(name, AllowedHeaders));
        }

        Assert.Equal("<html>ru</html>", await _client.GetStringAsync(new Uri(emulator.BaseAddress, "ru/"), Ct));
        using HttpResponseMessage page = await GetAsync(emulator, "");
        Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);
        using HttpResponseMessage json = await GetAsync(emulator, "data/en/index.0123456789abcdef.json");
        Assert.Equal("application/json", json.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task DirectoryWithoutSlash_Is301ToTheSlashedUrl_KeepingTheQuery()
    {
        await using PagesEmulator emulator = await StartAsync();

        using HttpResponseMessage response = await GetAsync(emulator, "ru?x=1");

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal("/dit/ru/?x=1", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Miss_Is404WithThe404HtmlBody_AndTheFileUrlWithASlashToo()
    {
        await using PagesEmulator emulator = await StartAsync();

        foreach (string path in new[] { "nope/", "ru/missing", "js/app.abcdefghij.js/" })
        {
            using HttpResponseMessage response = await GetAsync(emulator, path);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("<html>missing</html>", await response.Content.ReadAsStringAsync(Ct));
        }

        using HttpResponseMessage outside = await _client.GetAsync(new Uri(emulator.BaseAddress, "/other/"), Ct);
        Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);
        Assert.Equal("<html>missing</html>", await outside.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Miss_WithoutA404Page_IsAnEmpty404()
    {
        File.Delete(Path.Combine(_site.Root, "404.html"));
        await using PagesEmulator emulator = await StartAsync();

        using HttpResponseMessage response = await GetAsync(emulator, "nope/");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Head_SendsTheHeadersWithoutABody()
    {
        await using PagesEmulator emulator = await StartAsync();

        using HttpResponseMessage response = await GetAsync(emulator, "ru/", method: HttpMethod.Head);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("<html>ru</html>".Length, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task Gzip_CompressesTextJavaScriptJsonAndWasmAtLevelSix_NeverBrotliOrImages()
    {
        await using PagesEmulator emulator = await StartAsync(gzip: true);

        foreach ((string path, string file) in new[] { ("", "index.html"), ("js/app.abcdefghij.js", "js/app.abcdefghij.js"), ("_framework/a.abcdefghij.wasm", "_framework/a.abcdefghij.wasm"), ("data/en/index.0123456789abcdef.json", "data/en/index.0123456789abcdef.json") })
        {
            using HttpResponseMessage response = await GetAsync(emulator, path, gzip: true);

            Assert.Equal("gzip", Assert.Single(response.Content.Headers.ContentEncoding));
            byte[] body = await response.Content.ReadAsByteArrayAsync(Ct);
            Assert.Equal(Level6(File.ReadAllBytes(Path.Combine(_site.Root, file))).Length, body.Length);
            Assert.Equal(body.Length, response.Content.Headers.ContentLength);
        }

        foreach (string path in new[] { "_framework/a.abcdefghij.wasm.br", "og/card.abcdefghij.png" })
        {
            using HttpResponseMessage response = await GetAsync(emulator, path, gzip: true);
            Assert.Empty(response.Content.Headers.ContentEncoding);
        }

        // A client that does not ask for gzip gets the file as it is.
        using HttpResponseMessage plain = await GetAsync(emulator, "_framework/a.abcdefghij.wasm");
        Assert.Empty(plain.Content.Headers.ContentEncoding);
        Assert.Equal(6000, (await plain.Content.ReadAsByteArrayAsync(Ct)).Length);
    }

    [Fact]
    public async Task Gzip_GzipsWasmOnlyWhenOn()
    {
        await using PagesEmulator on = await StartAsync(gzip: true);
        await using PagesEmulator off = await StartAsync(gzip: false);

        using HttpResponseMessage compressed = await GetAsync(on, "_framework/a.abcdefghij.wasm", gzip: true);
        using HttpResponseMessage raw = await GetAsync(off, "_framework/a.abcdefghij.wasm", gzip: true);

        Assert.Contains("gzip", compressed.Content.Headers.ContentEncoding);
        Assert.Empty(raw.Content.Headers.ContentEncoding);
        Assert.Equal(6000, (await raw.Content.ReadAsByteArrayAsync(Ct)).Length);
    }

    [Fact]
    public async Task FailOnce_AnswersTheNextRequestWithTheStatusThenServesNormally()
    {
        await using PagesEmulator emulator = await StartAsync();
        emulator.FailOnce("/dit/js/app.abcdefghij.js", 503);

        using HttpResponseMessage first = await GetAsync(emulator, "js/app.abcdefghij.js");
        using HttpResponseMessage second = await GetAsync(emulator, "js/app.abcdefghij.js");
        using HttpResponseMessage other = await GetAsync(emulator, "ru/");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(4000, (await second.Content.ReadAsByteArrayAsync(Ct)).Length);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task Delay_HoldsMatchingPathsOnly()
    {
        await using PagesEmulator emulator = await StartAsync();
        emulator.Delay("/dit/data/", TimeSpan.FromMilliseconds(400));

        Stopwatch index = Stopwatch.StartNew();
        using HttpResponseMessage delayed = await GetAsync(emulator, "data/en/index.0123456789abcdef.json");
        index.Stop();
        Stopwatch page = Stopwatch.StartNew();
        using HttpResponseMessage fast = await GetAsync(emulator, "ru/");
        page.Stop();

        Assert.Equal(HttpStatusCode.OK, delayed.StatusCode);
        Assert.True(index.Elapsed >= TimeSpan.FromMilliseconds(380), $"the index answered after {index.Elapsed}");
        Assert.True(page.Elapsed < TimeSpan.FromMilliseconds(300), $"the page answered after {page.Elapsed}");
    }

    [Fact]
    public async Task SwapRoot_ServesTheNewDirectory()
    {
        using SiteDir next = new();
        next.Write("ru/index.html", "<html>ru, build B</html>");
        await using PagesEmulator emulator = await StartAsync();

        Assert.Equal("<html>ru</html>", await _client.GetStringAsync(new Uri(emulator.BaseAddress, "ru/"), Ct));
        emulator.SwapRoot(next.Root);

        Assert.Equal("<html>ru, build B</html>", await _client.GetStringAsync(new Uri(emulator.BaseAddress, "ru/"), Ct));
        using HttpResponseMessage gone = await GetAsync(emulator, "js/app.abcdefghij.js");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task ARemovedFile_IsGoneAtOnce()
    {
        await using PagesEmulator emulator = await StartAsync();
        using HttpResponseMessage before = await GetAsync(emulator, "js/app.abcdefghij.js");

        File.Delete(Path.Combine(_site.Root, "js", "app.abcdefghij.js"));
        using HttpResponseMessage after = await GetAsync(emulator, "js/app.abcdefghij.js");

        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }

    [Fact]
    public async Task MapExtra_ServesAFileOutsideTheRoot()
    {
        string extra = Path.Combine(Path.GetTempPath(), "dit-extra-" + Guid.NewGuid().ToString("N") + ".html");
        File.WriteAllText(extra, "<html>previous build</html>", Encoding.UTF8);
        try
        {
            await using PagesEmulator emulator = await StartAsync();
            emulator.MapExtra("/dit/__previous/", extra);

            using HttpResponseMessage response = await GetAsync(emulator, "__previous/");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal("<html>previous build</html>", await response.Content.ReadAsStringAsync(Ct));
        }
        finally
        {
            File.Delete(extra);
        }
    }

    [Fact]
    public async Task ARootSite_ServesFromTheRoot()
    {
        await using PagesEmulator emulator = await PagesEmulator.StartAsync(new PagesEmulatorOptions(_site.Root, new Uri("https://example.test/")));

        Assert.Equal("/", emulator.BaseAddress.AbsolutePath);
        Assert.Equal("<html>ru</html>", await _client.GetStringAsync(new Uri(emulator.BaseAddress, "ru/"), Ct));
    }

    [Fact]
    public async Task StartingOnAMissingDirectory_Throws() =>
        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => PagesEmulator.StartAsync(new PagesEmulatorOptions(Path.Combine(_site.Root, "nope"), Site)));

    private static byte[] Level6(byte[] bytes)
    {
        using MemoryStream compressed = new();
        using (GZipStream gzip = new(compressed, new ZLibCompressionOptions { CompressionLevel = 6 }, leaveOpen: true))
        {
            gzip.Write(bytes);
        }

        return compressed.ToArray();
    }
}
