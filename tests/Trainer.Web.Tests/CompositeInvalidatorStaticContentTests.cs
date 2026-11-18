using System.Text.Json;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Trainer.Application.Content;
using Trainer.Application.Stacks;
using Trainer.Application.StaticSite;
using Trainer.Contracts;
using Trainer.Domain.Keys;
using Trainer.Web.Caching;
using Trainer.Web.ContentState;
using Trainer.Web.OpenGraph;
using Trainer.Web.StaticSite;

namespace Trainer.Web.Tests;

/// <summary>
/// <see cref="CompositeContentCacheInvalidator"/> clears the static content catalog, also after an earlier invalidator in the
/// chain threw (it already collects failures and keeps going; nothing needed changing).
/// </summary>
public sealed class CompositeInvalidatorStaticContentTests
{
    private static readonly CultureKey En = new("en");
    private static readonly StackKey Dotnet = new("dotnet");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ContentHashOf(StaticContentFiles files) =>
        JsonSerializer.Deserialize(files.Index.Span, TrainerJsonContext.Default.StaticIndexDto)!.ContentHash;

    private static (ServiceProvider Provider, FakeContent Content) CreateServices()
    {
        FakeContent content = new() { Hash = "h1" };
        ServiceCollection services = new();
        services.AddScoped<IContentStatusQueries>(_ => content);
        services.AddScoped<IStaticSiteQueries>(_ => content);
        services.AddScoped<IStackQueries>(_ => content);
        return (services.BuildServiceProvider(), content);
    }

    private static CompositeContentCacheInvalidator CreateComposite(
        ServiceProvider provider, StaticContentCatalog catalog, IOutputCacheStore store, out ContentSnapshotProvider snapshots, out OgImageCache images)
    {
        snapshots = new ContentSnapshotProvider(provider.GetRequiredService<IServiceScopeFactory>());
        images = new OgImageCache(
            Microsoft.Extensions.Options.Options.Create(new OpenGraphOptions { CacheDirectory = Path.Combine(Path.GetTempPath(), "dit-og-" + Guid.NewGuid().ToString("N")) }),
            NullLogger<OgImageCache>.Instance);
        return new CompositeContentCacheInvalidator(new OutputCacheInvalidator(snapshots, store), images, catalog);
    }

    [Fact]
    public async Task Composite_ClearsTheStaticCatalog()
    {
        (ServiceProvider provider, FakeContent content) = CreateServices();
        await using ServiceProvider _ = provider;
        using StaticContentCatalog catalog = new(provider.GetRequiredService<IServiceScopeFactory>());
        CompositeContentCacheInvalidator composite = CreateComposite(provider, catalog, new QuietStore(), out ContentSnapshotProvider snapshots, out OgImageCache images);
        using ContentSnapshotProvider _1 = snapshots;
        using OgImageCache _2 = images;
        StaticContentFiles before = await catalog.GetAsync(En, Dotnet, Ct);

        content.Hash = "h2";
        // Nothing has invalidated yet, so the cached hash still answers.
        Assert.Same(before, await catalog.GetAsync(En, Dotnet, Ct));
        await composite.InvalidateAsync(Ct);
        StaticContentFiles after = await catalog.GetAsync(En, Dotnet, Ct);

        Assert.Equal("h1", ContentHashOf(before));
        Assert.Equal("h2", ContentHashOf(after));
    }

    [Fact]
    public async Task Composite_ClearsTheStaticCatalogEvenWhenAnEarlierInvalidatorThrows()
    {
        (ServiceProvider provider, FakeContent content) = CreateServices();
        await using ServiceProvider _ = provider;
        using StaticContentCatalog catalog = new(provider.GetRequiredService<IServiceScopeFactory>());
        CompositeContentCacheInvalidator composite = CreateComposite(provider, catalog, new ThrowingStore(), out ContentSnapshotProvider snapshots, out OgImageCache images);
        using ContentSnapshotProvider _1 = snapshots;
        using OgImageCache _2 = images;
        StaticContentFiles before = await catalog.GetAsync(En, Dotnet, Ct);
        content.Hash = "h2";

        AggregateException thrown = await Assert.ThrowsAsync<AggregateException>(() => composite.InvalidateAsync(Ct));
        StaticContentFiles after = await catalog.GetAsync(En, Dotnet, Ct);

        Assert.Contains(thrown.InnerExceptions, e => e is IOException { Message: "store is down" });
        Assert.NotSame(before, after);
        Assert.Equal("h2", ContentHashOf(after));
    }

    private sealed class FakeContent : IContentStatusQueries, IStaticSiteQueries, IStackQueries
    {
        public string Hash { get; set; } = string.Empty;

        public Task<string?> GetActiveHashAsync(CancellationToken ct) => Task.FromResult<string?>(Hash);

        public Task<IReadOnlyList<QuestionDto>> GetLiveQuestionsAsync(CultureKey culture, StackKey stack, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<QuestionDto>>([]);

        public Task<StaticPagePaths> GetPagePathsAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<StackKey, IReadOnlyList<TopicKey>>> GetTaxonomyAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<StackKey, IReadOnlyList<TopicKey>>>(new Dictionary<StackKey, IReadOnlyList<TopicKey>>());

        public Task<IReadOnlyList<TopicTitle>> GetTopicTitlesAsync(CultureKey culture, StackKey stack, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TopicTitle>>([]);
    }

    private class QuietStore : IOutputCacheStore
    {
        public virtual ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken) => ValueTask.FromResult<byte[]?>(null);

        public ValueTask SetAsync(string key, byte[] value, string[]? tags, TimeSpan validFor, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class ThrowingStore : QuietStore
    {
        public override ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken) => throw new IOException("store is down");
    }
}
