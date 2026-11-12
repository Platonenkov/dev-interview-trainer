using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Trainer.Application.Stacks;
using Trainer.Domain.Keys;
using Trainer.Web.Caching;
using Trainer.Web.ContentState;

namespace Trainer.Web.Tests;

public sealed class OutputCacheInvalidatorTests
{
    private sealed class ThrowingStackQueries : IStackQueries
    {
        public Task<IReadOnlyDictionary<StackKey, IReadOnlyList<TopicKey>>> GetTaxonomyAsync(CancellationToken ct) =>
            throw new InvalidOperationException("database is down");
    }

    private sealed class RecordingStore : IOutputCacheStore
    {
        public List<string> EvictedTags { get; } = [];

        public ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken)
        {
            EvictedTags.Add(tag);
            return ValueTask.CompletedTask;
        }

        public ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken) => ValueTask.FromResult<byte[]?>(null);

        public ValueTask SetAsync(string key, byte[] value, string[]? tags, TimeSpan validFor, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class ThrowingStore : IOutputCacheStore
    {
        public ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken) => throw new IOException("store is down");

        public ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken) => ValueTask.FromResult<byte[]?>(null);

        public ValueTask SetAsync(string key, byte[] value, string[]? tags, TimeSpan validFor, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private static ContentSnapshotProvider ProviderWith(IStackQueries queries)
    {
        ServiceCollection services = new();
        services.AddSingleton(queries);
        return new ContentSnapshotProvider(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
    }

    [Fact]
    public async Task SnapshotRefreshFails_StillEvictsTheContentTag_AndRethrows()
    {
        RecordingStore store = new();
        using ContentSnapshotProvider snapshots = ProviderWith(new ThrowingStackQueries());
        OutputCacheInvalidator invalidator = new(snapshots, store);

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => invalidator.InvalidateAsync(TestContext.Current.CancellationToken));

        Assert.Equal("database is down", thrown.Message);
        Assert.Equal([OutputCacheInvalidator.ContentTag], store.EvictedTags);
    }

    [Fact]
    public async Task RefreshAndEvictionBothFail_SurfacesBothExceptions()
    {
        using ContentSnapshotProvider snapshots = ProviderWith(new ThrowingStackQueries());
        OutputCacheInvalidator invalidator = new(snapshots, new ThrowingStore());

        AggregateException thrown = await Assert.ThrowsAsync<AggregateException>(
            () => invalidator.InvalidateAsync(TestContext.Current.CancellationToken));

        Assert.Contains(thrown.InnerExceptions, e => e is InvalidOperationException { Message: "database is down" });
        Assert.Contains(thrown.InnerExceptions, e => e is IOException { Message: "store is down" });
    }

    [Fact]
    public async Task Cancelled_StillEvictsTheContentTag()
    {
        RecordingStore store = new();
        using ContentSnapshotProvider snapshots = ProviderWith(new ThrowingStackQueries());
        OutputCacheInvalidator invalidator = new(snapshots, store);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invalidator.InvalidateAsync(cancelled.Token));

        Assert.Equal([OutputCacheInvalidator.ContentTag], store.EvictedTags);
    }
}
