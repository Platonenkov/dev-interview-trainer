using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Trainer.Contracts;
using Trainer.Domain.Scheduling;
using Trainer.Domain.Sessions;
using Trainer.Web.Client.Api;
using Trainer.Web.Client.Backend;
using Trainer.Web.Client.Storage;

namespace Trainer.Web.Client.Tests;

/// <summary>
/// A static site in memory: an index and one file per question served through <see cref="FakeHttpHandler"/>, a browser
/// store that tabs share, and the static backend wired over them.
/// </summary>
internal sealed class StaticSiteFixture
{
    public const string IndexPath = "data/en/dotnet/index.0123456789abcdef.json";

    public static readonly DateTimeOffset Start = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private static int _answers;

    public StaticSiteFixture(params QuestionDto[] questions)
    {
        Questions = questions;
        Index = new StaticIndexDto(
            StaticIndexDto.CurrentFormat,
            "en",
            "dotnet",
            "c0ffee",
            [.. questions.Select(q => new TopicDto(q.Topic, q.TopicTitle)).DistinctBy(t => t.Key)],
            [.. questions.OrderBy(q => q.Id, StringComparer.Ordinal).Select(q => new StaticIndexEntryDto(q.Id, q.Slug, q.Topic, q.Level, q.Type, q.Title, HashOf(q)))]);
        Handler.Respond = Serve;
        Store = new FakeBrowserStore();
        Time = new FakeTimeProvider(Start);
    }

    public IReadOnlyList<QuestionDto> Questions { get; }

    public StaticIndexDto Index { get; set; }

    public FakeHttpHandler Handler { get; } = new();

    public FakeBrowserStore Store { get; }

    public FakeTimeProvider Time { get; }

    public static StaticSiteDto Site => new(IndexPath, "https://github.com/o/r/issues/new");

    /// <summary>Paths of the question files requested so far, in order.</summary>
    public IReadOnlyList<string> QuestionRequests => [.. Handler.Requests.Select(r => r.Path).Where(p => p.Contains("/q/", StringComparison.Ordinal))];

    public IReadOnlyList<string> IndexRequests => [.. Handler.Requests.Select(r => r.Path).Where(p => p.Contains("/index.", StringComparison.Ordinal))];

    public static string QuestionPath(QuestionDto question) => "/" + StaticContentPaths.Question("en", question.Id, HashOf(question));

    public static string BootstrapJson() => IslandHarness.BootstrapJson(null, Site);

    /// <summary>One browser tab: its own store handle onto the shared storage, outbox, cache and backend.</summary>
    public Tab NewTab(Random? random = null, CountingScheduler? scheduler = null) => new(this, random ?? new Random(7), scheduler);

    public static AnswerDto Answer(QuestionDto question, string grade, DateTimeOffset at, string? id = null) =>
        new(id ?? NewAnswerId(), question.Id, grade, false, "practice", null, at);

    public static string NewAnswerId() => "01JD" + Interlocked.Increment(ref _answers).ToString("D22", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Registers this site's pieces into a harness: its HTTP client, store, backend and the services under it.</summary>
    public void Configure(IServiceCollection services, HookStore store)
    {
        services.AddSingleton(Handler.Client());
        services.AddSingleton<IBrowserStore>(store);
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton<IReviewScheduler, Sm2ReviewScheduler>();
        services.AddSingleton(new PracticeSelector(new Random(7)));
        services.AddSingleton(new MockComposer(new Random(7)));
        services.AddScoped<StaticContentClient>();
        services.AddScoped<LocalProgressCache>();
        services.AddScoped<IIslandBackend, StaticIslandBackend>();
    }

    private static string HashOf(QuestionDto question) => question.Id[^16..].ToLowerInvariant();

    private HttpResponseMessage? Serve(RecordedRequest request)
    {
        if (string.Equals(request.Path, "/" + IndexPath, StringComparison.Ordinal))
        {
            return FakeHttpHandler.Json(HttpStatusCode.OK, JsonSerializer.Serialize(Index, TrainerJsonContext.Default.StaticIndexDto));
        }

        QuestionDto? question = Questions.FirstOrDefault(q => string.Equals(request.Path, QuestionPath(q), StringComparison.Ordinal));
        return question is null
            ? FakeHttpHandler.Error(HttpStatusCode.NotFound, ApiErrorCodes.NotFound)
            : FakeHttpHandler.Json(HttpStatusCode.OK, JsonSerializer.Serialize(question, TrainerJsonContext.Default.QuestionDto));
    }

    internal sealed class Tab
    {
        public Tab(StaticSiteFixture site, Random random, CountingScheduler? scheduler)
        {
            Hook = new HookStore(site.Store);
            Outbox = new OutboxStore(Hook);
            Scheduler = scheduler ?? new CountingScheduler();
            History = new LocalMockHistoryStore(Hook);
            Progress = new LocalProgressCache(Outbox, Hook, Scheduler, site.Time, TimeSpan.Zero, History);
            Content = new StaticContentClient(site.Handler.Client());
            Backend = new StaticIslandBackend(Content, Progress, History, new PracticeSelector(random), new MockComposer(random), site.Time);
        }

        public HookStore Hook { get; }

        public OutboxStore Outbox { get; }

        public CountingScheduler Scheduler { get; }

        public LocalProgressCache Progress { get; }

        public StaticContentClient Content { get; }

        public LocalMockHistoryStore History { get; }

        public StaticIslandBackend Backend { get; }
    }
}

/// <summary>The real scheduler that counts how many answers it was asked to apply.</summary>
internal sealed class CountingScheduler : IReviewScheduler
{
    private readonly Sm2ReviewScheduler _inner = new();

    public int Applied { get; private set; }

    public ReviewState Apply(ReviewState? current, Trainer.Domain.Grading.Grade grade, DateTimeOffset answeredAt)
    {
        Applied++;
        return _inner.Apply(current, grade, answeredAt);
    }
}
