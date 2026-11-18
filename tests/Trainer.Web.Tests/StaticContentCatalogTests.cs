using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Trainer.Application.Content;
using Trainer.Application.Stacks;
using Trainer.Application.StaticSite;
using Trainer.Contracts;
using Trainer.Domain.Keys;
using Trainer.Web.StaticSite;

namespace Trainer.Web.Tests;

/// <summary>The catalog's hash cache, invalidation and build retry, against fakes: no database, no host.</summary>
public sealed class StaticContentCatalogTests
{
    private static readonly CultureKey En = new("en");
    private static readonly StackKey Dotnet = new("dotnet");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StaticContentCatalog CreateCatalog(FakeContent content)
    {
        ServiceCollection services = new();
        services.AddScoped<IContentStatusQueries>(_ => content);
        services.AddScoped<IStaticSiteQueries>(_ => content);
        services.AddScoped<IStackQueries>(_ => content);
        return new StaticContentCatalog(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
    }

    private static string ContentHashOf(StaticContentFiles files) =>
        JsonSerializer.Deserialize(files.Index.Span, TrainerJsonContext.Default.StaticIndexDto)!.ContentHash;

    [Fact]
    public async Task CacheHit_DoesNoDatabaseRoundTrip()
    {
        FakeContent content = new(["h1"]);
        using StaticContentCatalog catalog = CreateCatalog(content);

        StaticContentFiles first = await catalog.GetAsync(En, Dotnet, Ct);
        int hashReadsAfterBuild = content.HashReads;
        int builds = content.QuestionReads;
        StaticContentFiles second = await catalog.GetAsync(En, Dotnet, Ct);
        StaticContentFiles third = await catalog.GetAsync(En, Dotnet, Ct);

        Assert.Same(first, second);
        Assert.Same(first, third);
        Assert.Equal(hashReadsAfterBuild, content.HashReads);
        Assert.Equal(builds, content.QuestionReads);
        Assert.Equal(1, builds);
    }

    [Fact]
    public async Task Invalidate_ReadsTheNewHashAndRebuilds()
    {
        FakeContent content = new(["h1"]);
        using StaticContentCatalog catalog = CreateCatalog(content);
        StaticContentFiles before = await catalog.GetAsync(En, Dotnet, Ct);

        content.SetActive("h2");
        // Without an invalidation the cached hash is still current: that is the saved round-trip.
        Assert.Same(before, await catalog.GetAsync(En, Dotnet, Ct));
        await catalog.InvalidateAsync(Ct);
        StaticContentFiles after = await catalog.GetAsync(En, Dotnet, Ct);

        Assert.Equal("h1", ContentHashOf(before));
        Assert.Equal("h2", ContentHashOf(after));
        Assert.NotEqual(before.IndexPath, after.IndexPath);
        Assert.Equal(2, content.QuestionReads);
    }

    [Fact]
    public async Task HashRead_ThatStartedBeforeAnInvalidation_IsNotCached()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeContent content = new(["old"]) { HoldFirstHashRead = release.Task };
        using StaticContentCatalog catalog = CreateCatalog(content);

        Task<StaticContentFiles> slow = catalog.GetAsync(En, Dotnet, Ct);
        await content.FirstHashReadStarted.Task.WaitAsync(Ct);
        content.SetActive("new");
        await catalog.InvalidateAsync(Ct);
        release.SetResult();
        StaticContentFiles built = await slow;
        int buildsBefore = content.QuestionReads;
        StaticContentFiles next = await catalog.GetAsync(En, Dotnet, Ct);

        // Had the late "old" read been remembered as the active hash, it would disagree with the entry built from "new"
        // and force another build here.
        Assert.Equal("new", ContentHashOf(built));
        Assert.Same(built, next);
        Assert.Equal(buildsBefore, content.QuestionReads);
    }

    [Fact]
    public async Task Build_RetriesWhenAnImportLandsBetweenTheHashAndTheQuestions()
    {
        // Reads: the active hash (a), then the build's before (a) and after (b) disagree, so it retries with b and b.
        FakeContent content = new(["a", "a", "b", "b", "b"]);
        using StaticContentCatalog catalog = CreateCatalog(content);

        StaticContentFiles files = await catalog.GetAsync(En, Dotnet, Ct);

        Assert.Equal("b", ContentHashOf(files));
        Assert.Equal(2, content.QuestionReads);
    }

    [Fact]
    public async Task Build_GivesUpWhenTheContentKeepsChanging()
    {
        FakeContent content = new(["a", "a", "b", "b", "c", "c", "d", "d", "e", "e"]);
        using StaticContentCatalog catalog = CreateCatalog(content);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.GetAsync(En, Dotnet, Ct));

        Assert.Contains("kept changing", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, content.QuestionReads);
    }

    [Fact]
    public async Task NoImport_ThrowsInsteadOfCaching()
    {
        FakeContent content = new([]);
        using StaticContentCatalog catalog = CreateCatalog(content);

        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.GetAsync(En, Dotnet, Ct));
        content.SetActive("h1");

        Assert.Equal("h1", ContentHashOf(await catalog.GetAsync(En, Dotnet, Ct)));
    }

    /// <summary>
    /// Serves hashes from a script (the last one repeats) and counts reads; <see cref="SetActive"/> replaces the script
    /// with one hash.
    /// </summary>
    private sealed class FakeContent(IEnumerable<string> hashes) : IContentStatusQueries, IStaticSiteQueries, IStackQueries
    {
        private readonly Lock _gate = new();
        private Queue<string> _script = new(hashes);
        private string? _last = hashes.LastOrDefault();
        private int _hashReads;
        private int _questionReads;

        public int HashReads => Volatile.Read(ref _hashReads);

        public int QuestionReads => Volatile.Read(ref _questionReads);

        public Task? HoldFirstHashRead { get; init; }

        public TaskCompletionSource FirstHashReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SetActive(string hash)
        {
            lock (_gate)
            {
                _script = new Queue<string>();
                _last = hash;
            }
        }

        public async Task<string?> GetActiveHashAsync(CancellationToken ct)
        {
            int number = Interlocked.Increment(ref _hashReads);
            string? value;
            lock (_gate)
            {
                value = _script.Count > 0 ? _script.Dequeue() : _last;
            }

            if (number == 1 && HoldFirstHashRead is not null)
            {
                FirstHashReadStarted.SetResult();
                await HoldFirstHashRead;
            }

            return value;
        }

        public Task<IReadOnlyList<QuestionDto>> GetLiveQuestionsAsync(CultureKey culture, StackKey stack, CancellationToken ct)
        {
            Interlocked.Increment(ref _questionReads);
            return Task.FromResult<IReadOnlyList<QuestionDto>>([]);
        }

        public Task<StaticPagePaths> GetPagePathsAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<StackKey, IReadOnlyList<TopicKey>>> GetTaxonomyAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<TopicTitle>> GetTopicTitlesAsync(CultureKey culture, StackKey stack, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TopicTitle>>([new TopicTitle(new TopicKey("b"), "B"), new TopicTitle(new TopicKey("a"), "A")]);
    }
}
