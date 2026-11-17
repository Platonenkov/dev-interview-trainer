using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using SkiaSharp;
using Trainer.Application.Content;
using Trainer.Application.Stacks;
using Trainer.Domain.Keys;
using Trainer.TestSupport;
using Trainer.Web.Caching;
using Trainer.Web.ContentState;
using Trainer.Web.OpenGraph;

namespace Trainer.Web.Tests;

public sealed partial class OgImageEndpointTests(WebSiteFixture site, PostgresFixture postgres) : IClassFixture<WebSiteFixture>
{
    private const string Origin = "https://trainer.test";
    private const string QuestionPage = "/ru/dotnet/questions/closure-for-loop";

    [GeneratedRegex(@"<meta property=""og:image"" content=""([^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex OgImage();

    private static async Task<string> AdvertisedPathAsync(TrainerWebFactory factory, string page)
    {
        using HttpClient client = factory.CreateNonRedirectingClient();
        string body = await client.GetStringAsync(page, TestContext.Current.CancellationToken);
        string url = OgImage().Match(body).Groups[1].Value;
        Assert.StartsWith(Origin + "/og/", url, StringComparison.Ordinal);
        return url[Origin.Length..];
    }

    private static async Task<TrainerWebFactory> CreateFactoryAsync(PostgresFixture postgres, string cacheDirectory, CapturingLoggerProvider? logs = null)
    {
        TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(
            postgres,
            new TrainerWebFactoryOptions
            {
                Settings = new Dictionary<string, string?> { ["App:OpenGraph:CacheDirectory"] = cacheDirectory },
                Logs = logs,
            });
        await factory.WaitUntilReadyAsync();
        return factory;
    }

    private static string NewCacheDirectory() => Path.Combine(Path.GetTempPath(), "dit-og-endpoint-" + Guid.NewGuid().ToString("N"));

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task ImportAsync(TrainerWebFactory factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        IContentImportService import = scope.ServiceProvider.GetRequiredService<IContentImportService>();
        ContentImportResult result = await import.ImportAsync(factory.Content.Root, TestContext.Current.CancellationToken);
        Assert.Equal(ContentImportStatus.Imported, result.Status);
    }

    [Fact]
    public async Task Endpoint_CurrentFingerprint_Returns200ImmutableNoCookie()
    {
        string path = await AdvertisedPathAsync(site.Factory, QuestionPage);
        using HttpClient client = site.Factory.CreateNonRedirectingClient();

        using HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        byte[] png = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("public, max-age=31536000, immutable", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains(HeaderNames.SetCookie));
        using SKBitmap bitmap = SKBitmap.Decode(png);
        Assert.Equal((1200, 630), (bitmap.Width, bitmap.Height));
    }

    [Fact]
    public async Task Endpoint_SiteCard_Returns200ForBothCulturesAndTheCardsDiffer()
    {
        string ruPath = await AdvertisedPathAsync(site.Factory, "/ru");
        string enPath = await AdvertisedPathAsync(site.Factory, "/en");
        using HttpClient client = site.Factory.CreateNonRedirectingClient();

        using HttpResponseMessage ru = await client.GetAsync(ruPath, TestContext.Current.CancellationToken);
        using HttpResponseMessage en = await client.GetAsync(enPath, TestContext.Current.CancellationToken);

        Assert.StartsWith("/og/ru/site/", ruPath, StringComparison.Ordinal);
        Assert.StartsWith("/og/en/site/", enPath, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, ru.StatusCode);
        Assert.Equal(HttpStatusCode.OK, en.StatusCode);
        Assert.NotEqual(
            await ru.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken),
            await en.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Endpoint_StaleFingerprint_Redirects301ToCurrent()
    {
        string current = await AdvertisedPathAsync(site.Factory, QuestionPage);
        string stale = "/og/ru/dotnet/closure-for-loop/0000000000000000.png";
        using HttpClient client = site.Factory.CreateNonRedirectingClient();

        using HttpResponseMessage response = await client.GetAsync(stale, TestContext.Current.CancellationToken);
        using HttpResponseMessage siteResponse = await client.GetAsync("/og/en/site/0000000000000000.png", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal(current, response.Headers.Location?.OriginalString);
        Assert.Equal("public, max-age=3600", response.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.MovedPermanently, siteResponse.StatusCode);
        Assert.StartsWith("/og/en/site/", siteResponse.Headers.Location?.OriginalString, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/og/ru/dotnet/en-only-question/0000000000000000.png")]
    [InlineData("/og/en/dotnet/no-such-question/0000000000000000.png")]
    [InlineData("/og/en/no-such-stack/closure-for-loop/0000000000000000.png")]
    [InlineData("/og/en/dotnet/Not_A_Slug/0000000000000000.png")]
    [InlineData("/og/en/unknown")]
    public async Task Endpoint_MissingLocaleOrUnknownTarget_Returns404WithEmptyBody(string path)
    {
        using HttpClient client = site.Factory.CreateNonRedirectingClient();

        using HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Endpoint_RetiredQuestion_Returns404AndThePageAdvertisesTheSiteCard()
    {
        string directory = NewCacheDirectory();
        try
        {
            await using TrainerWebFactory factory = await CreateFactoryAsync(postgres, directory);
            string beforeRetiring = await AdvertisedPathAsync(factory, "/en/dotnet/questions/to-be-retired");
            factory.Content.Delete("dotnet/async/to-be-retired.yaml");
            await ImportAsync(factory);
            using HttpClient client = factory.CreateNonRedirectingClient();

            using HttpResponseMessage image = await client.GetAsync(beforeRetiring, TestContext.Current.CancellationToken);
            string afterRetiring = await AdvertisedPathAsync(factory, "/en/dotnet/questions/to-be-retired");

            Assert.StartsWith("/og/en/dotnet/to-be-retired/", beforeRetiring, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.NotFound, image.StatusCode);
            Assert.Empty(await image.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
            Assert.StartsWith("/og/en/site/", afterRetiring, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Theory]
    [InlineData("/og/de/dotnet/closure-for-loop/0000000000000000.png")]
    [InlineData("/og/de/site/0000000000000000.png")]
    [InlineData("/og/EN/site/0000000000000000.png")]
    public async Task Endpoint_UnsupportedCulture_Returns404WithEmptyBody(string path)
    {
        using HttpClient client = site.Factory.CreateNonRedirectingClient();

        using HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        // The route values are not named culture or stack, so the culture middleware cannot turn this into the HTML page (P11).
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Null(response.Content.Headers.ContentType);
    }

    [Fact]
    public async Task Endpoint_AfterImportChangingTitle_NewFingerprintAndCacheCleared()
    {
        string directory = NewCacheDirectory();
        try
        {
            await using TrainerWebFactory factory = await CreateFactoryAsync(postgres, directory);
            string oldPath = await AdvertisedPathAsync(factory, "/en/dotnet/questions/closure-for-loop");
            using HttpClient client = factory.CreateNonRedirectingClient();
            using HttpResponseMessage first = await client.GetAsync(oldPath, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.NotEmpty(Directory.EnumerateFiles(directory, "*.png"));

            factory.Content.Edit(
                "dotnet/csharp/closure-for-loop.yaml",
                "What does the code print when lambdas capture a for loop variable?",
                "What does this code print when lambdas capture the loop variable?");
            await ImportAsync(factory);

            Assert.Empty(Directory.EnumerateFiles(directory));
            using HttpResponseMessage stale = await client.GetAsync(oldPath, TestContext.Current.CancellationToken);
            string newPath = await AdvertisedPathAsync(factory, "/en/dotnet/questions/closure-for-loop");
            using HttpResponseMessage fresh = await client.GetAsync(newPath, TestContext.Current.CancellationToken);

            Assert.NotEqual(oldPath, newPath);
            Assert.Equal(HttpStatusCode.MovedPermanently, stale.StatusCode);
            Assert.Equal(newPath, stale.Headers.Location?.OriginalString);
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task Endpoint_UnwritableCacheDirectory_StillServesTheImageAndLogsAWarning()
    {
        string blocker = Path.Combine(Path.GetTempPath(), "dit-og-blocker-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(blocker, "a file where the cache directory should be", TestContext.Current.CancellationToken);
        try
        {
            CapturingLoggerProvider logs = new();
            await using TrainerWebFactory factory = await CreateFactoryAsync(postgres, Path.Combine(blocker, "og"), logs);
            string path = await AdvertisedPathAsync(factory, QuestionPage);
            using HttpClient client = factory.CreateNonRedirectingClient();

            using HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
            Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Category == typeof(OgImageCache).FullName);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public async Task Endpoint_OnlyGetAndHeadServeImages()
    {
        string path = await AdvertisedPathAsync(site.Factory, QuestionPage);
        using HttpClient client = site.Factory.CreateNonRedirectingClient();

        using HttpRequestMessage head = new(HttpMethod.Head, path);
        using HttpResponseMessage headResponse = await client.SendAsync(head, TestContext.Current.CancellationToken);
        using HttpResponseMessage post = await client.PostAsync(path, new StringContent(string.Empty), TestContext.Current.CancellationToken);
        using HttpRequestMessage delete = new(HttpMethod.Delete, path);
        using HttpResponseMessage deleteResponse = await client.SendAsync(delete, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);
        Assert.Empty(await headResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deleteResponse.StatusCode);
        Assert.Empty(await post.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Endpoint_SignedInRequestWithRenewableCookie_NeverGetsSetCookieOrAnotherCachePolicy()
    {
        await using SessionSite session = await SessionSite.StartAsync(postgres);
        (HttpClient client, HttpResponseMessage callback) = await session.SignInAsync(
            new FakeTelegramIdentity(770001, "Og Person", "og_person"), TestContext.Current.CancellationToken);
        using HttpClient disposableClient = client;
        callback.Dispose();
        string path = await AdvertisedPathAsync(session.Factory, QuestionPage);

        // Twenty days in, the sliding rule reissues the cookie on any request the handler sees.
        session.Clock.Advance(TimeSpan.FromDays(20));
        using HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        using HttpResponseMessage stale = await client.GetAsync("/og/ru/dotnet/closure-for-loop/0000000000000000.png", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains(HeaderNames.SetCookie));
        Assert.Equal("public, max-age=31536000, immutable", response.Headers.CacheControl?.ToString());
        Assert.False(stale.Headers.Contains(HeaderNames.SetCookie));
        Assert.Equal("public, max-age=3600", stale.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Endpoint_AllRenderSlotsBusy_Answers503NoStoreWithRetryAfter()
    {
        string directory = NewCacheDirectory();
        try
        {
            await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(
                postgres,
                new TrainerWebFactoryOptions
                {
                    ConfigureServices = services => services.AddSingleton(provider => new OgImageCache(
                        Microsoft.Extensions.Options.Options.Create(new OpenGraphOptions { CacheDirectory = directory }),
                        provider.GetRequiredService<ILogger<OgImageCache>>(),
                        TimeSpan.FromMilliseconds(100))),
                });
            await factory.WaitUntilReadyAsync();
            string path = await AdvertisedPathAsync(factory, QuestionPage);
            OgImageCache cache = factory.Services.GetRequiredService<OgImageCache>();
            using ManualResetEventSlim release = new();
            Task[] holders = [.. Enumerable.Range(0, OgImageCache.MaxConcurrentRenders).Select(i =>
                cache.GetOrRenderAsync($"en.site.hold{i}.png", () => { release.Wait(); return [1]; }, TestContext.Current.CancellationToken))];
            await Task.Delay(200, TestContext.Current.CancellationToken);
            using HttpClient client = factory.CreateNonRedirectingClient();

            using HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.True(response.Headers.Contains(HeaderNames.RetryAfter));
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

            release.Set();
            await Task.WhenAll(holders);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task Composite_OneFailing_StillRunsTheOtherAndReportsAnAggregate()
    {
        string directory = NewCacheDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, "en-site-x.png"), [1], TestContext.Current.CancellationToken);
            ServiceCollection services = new();
            services.AddSingleton<IStackQueries>(new EmptyStackQueries());
            using ContentSnapshotProvider snapshots = new(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
            OutputCacheInvalidator pages = new(snapshots, new ThrowingStore());
            using OgImageCache images = new(
                Microsoft.Extensions.Options.Options.Create(new OpenGraphOptions { CacheDirectory = directory }),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<OgImageCache>.Instance);
            CompositeContentCacheInvalidator composite = new(pages, images);

            AggregateException thrown = await Assert.ThrowsAsync<AggregateException>(
                () => composite.InvalidateAsync(TestContext.Current.CancellationToken));

            Assert.Contains(thrown.InnerExceptions, e => e is IOException { Message: "store is down" });
            Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private sealed class EmptyStackQueries : IStackQueries
    {
        public Task<IReadOnlyDictionary<StackKey, IReadOnlyList<TopicKey>>> GetTaxonomyAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<StackKey, IReadOnlyList<TopicKey>>>(new Dictionary<StackKey, IReadOnlyList<TopicKey>>());

        public Task<IReadOnlyList<TopicTitle>> GetTopicTitlesAsync(CultureKey culture, StackKey stack, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TopicTitle>>([]);
    }

    private sealed class ThrowingStore : Microsoft.AspNetCore.OutputCaching.IOutputCacheStore
    {
        public ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken) => throw new IOException("store is down");

        public ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken) => ValueTask.FromResult<byte[]?>(null);

        public ValueTask SetAsync(string key, byte[] value, string[]? tags, TimeSpan validFor, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
