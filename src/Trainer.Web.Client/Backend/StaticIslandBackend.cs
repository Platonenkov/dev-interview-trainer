using Trainer.Contracts;
using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;
using Trainer.Domain.Sessions;
using Trainer.Web.Client.Api;
using Trainer.Web.Client.Storage;

namespace Trainer.Web.Client.Backend;

/// <summary>
/// The static edition's backend: sessions are built in the browser from the content index with the same rules the server
/// runs (<see cref="PracticeSelector"/>, <see cref="ReviewOrder"/>, <see cref="MockComposer"/>), the questions are fetched
/// from the content shards as the session reaches them, and progress is the replayed answer log
/// (<see cref="LocalProgressCache"/>). Every answer goes to the anonymous outbox, which is never sent anywhere; the
/// finished mocks are kept by <see cref="LocalMockHistoryStore"/>.
/// </summary>
public sealed class StaticIslandBackend(
    StaticContentClient content,
    LocalProgressCache progress,
    LocalMockHistoryStore history,
    PracticeSelector selector,
    MockComposer composer,
    TimeProvider time) : IIslandBackend
{
    // How many question files a mock fetches at once; the browser limits parallel requests per host anyway.
    private const int MockFetchParallelism = 6;

    public bool KeepsProgress(IslandBootstrapDto bootstrap) => true;

    public string OutboxKey(IslandBootstrapDto bootstrap) => StorageKeys.AnonymousOutbox;

    // Every answer is already applied locally by the replay, so an answer waiting in the outbox is never "not yet counted".
    public bool FiltersPendingAnswers => false;

    // The selector sees the replayed states, so the status filters need no list of answered ids.
    public bool SendsAnsweredAsExclude => false;

    public async Task<IslandProgress?> GetProgressAsync(IslandBootstrapDto bootstrap, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        // Both start now: the index is a network fetch and the replay a storage read, and the island waits for each.
        Task<ApiResult<StaticIndexDto>> indexTask = LoadIndexAsync(bootstrap, ct);
        Task<LocalProgress> progressTask = progress.GetAsync(ct).AsTask();
        ApiResult<StaticIndexDto> index = await indexTask;
        LocalProgress local = await progressTask;
        return index.Value is { } value ? local.Summarize(value, time.GetUtcNow(), Offset, bootstrap.Topics) : null;
    }

    public async Task<ApiResult<SessionPlan>> PracticeAsync(IslandBootstrapDto bootstrap, PracticeSessionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(request);
        Task<LocalProgress> progressTask = progress.GetAsync(ct).AsTask();
        ApiResult<StaticIndexDto> loaded = await LoadIndexAsync(bootstrap, ct);
        if (loaded.Value is not { } index)
        {
            Observe(progressTask);
            return Failure<SessionPlan>(loaded);
        }

        PracticeSelection selection = ToSelection(request);
        IQuestionSource questions = new StaticQuestionSource(content, index);
        IReadOnlyList<PracticeCandidate> candidates = StaticIndex.Candidates(index);
        IReadOnlySet<QuestionId> live = StaticIndex.LiveIds(index);
        if (selection.First is { } first && live.Contains(first))
        {
            // The opening question needs the index but not the progress; the rest of the session is picked when it is ready.
            Task<SessionRemainder> remainder = SelectRemainderAsync(progressTask, candidates, live, selection);
            return Success(new SessionPlan([first.Value], 1, null, questions, remainder));
        }

        LocalProgress local = await progressTask;
        PracticePick pick = selector.Select(candidates, selection, local.DueAtById(live), time.GetUtcNow());
        return Success(new SessionPlan([.. pick.Ids.Select(id => id.Value)], pick.PoolSize, null, questions));
    }

    public async Task<ApiResult<MockSessionDto>> MockAsync(IslandBootstrapDto bootstrap, MockSessionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(request);
        ApiResult<StaticIndexDto> loaded = await LoadIndexAsync(bootstrap, ct);
        if (loaded.Value is not { } index)
        {
            return Failure<MockSessionDto>(loaded);
        }

        if (!LevelKeys.TryParse(request.Level, out Level level))
        {
            return new ApiResult<MockSessionDto>(null, 400, ApiErrorCodes.Validation);
        }

        HashSet<TopicKey> topics = [.. request.Topics.Select(ParseTopic).OfType<TopicKey>()];
        // Junior has no lower level; level 0 matches no question.
        Level below = (Level)((int)level - 1);
        PracticeCandidate[] pool =
        [
            .. StaticIndex.Candidates(index)
                .Where(c => (c.Level == level || c.Level == below) && (topics.Count == 0 || topics.Contains(c.Topic))),
        ];
        QuestionId[] atLevel = [.. pool.Where(c => c.Level == level).Select(c => c.Id)];
        QuestionId[] lower = [.. pool.Where(c => c.Level == below).Select(c => c.Id)];
        int count = Math.Clamp(request.Count, SessionLimits.MinMockCount, SessionLimits.MaxCount);
        IReadOnlyList<QuestionId> chosen = composer.Compose(level, atLevel, lower, count);

        ApiResult<QuestionDto>[] fetched = await FetchAllAsync(content, index, chosen, ct);
        if (fetched.FirstOrDefault(r => r.Value is null || !r.IsSuccess) is { } failed)
        {
            return Failure<MockSessionDto>(failed);
        }

        int poolSize = atLevel.Length + lower.Length;
        return Success(new MockSessionDto(
            [.. fetched.Select(r => r.Value!)],
            poolSize,
            MockComposer.RepeatsLikely(poolSize, count),
            MockComposer.SuggestedCount(poolSize)));
    }

    public async Task<ApiResult<SessionPlan>> ReviewAsync(IslandBootstrapDto bootstrap, ReviewSessionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(request);
        Task<LocalProgress> progressTask = progress.GetAsync(ct).AsTask();
        ApiResult<StaticIndexDto> loaded = await LoadIndexAsync(bootstrap, ct);
        if (loaded.Value is not { } index)
        {
            Observe(progressTask);
            return Failure<SessionPlan>(loaded);
        }

        LocalProgress local = await progressTask;
        int count = Math.Clamp(request.Count, SessionLimits.MinReviewCount, SessionLimits.MaxCount);
        (IReadOnlyList<QuestionId> ids, int poolSize) = ReviewOrder.Pick(local.DueStates(), StaticIndex.LiveIds(index), time.GetUtcNow(), count);
        return Success(new SessionPlan([.. ids.Select(id => id.Value)], poolSize, null, new StaticQuestionSource(content, index)));
    }

    public async Task<ApiResult<ProfileDto>> ProfileAsync(IslandBootstrapDto bootstrap, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        Task<LocalProgress> progressTask = progress.GetAsync(ct).AsTask();
        ApiResult<StaticIndexDto> loaded = await LoadIndexAsync(bootstrap, ct);
        if (loaded.Value is not { } index)
        {
            Observe(progressTask);
            return Failure<ProfileDto>(loaded);
        }

        LocalProgress local = await progressTask;
        IReadOnlyList<MockHistoryDto> mocks = await history.ReadAsync(bootstrap.Stack);
        return Success(LocalProfileBuilder.Build(index, local, mocks, time.GetUtcNow(), Offset, bootstrap.Topics));
    }

    public Task OnSessionFinishedAsync(IslandBootstrapDto bootstrap, CancellationToken ct) => Task.CompletedTask;

    // A failed write is not an error here: the store keeps the mock in memory and StorageNotice says so.
    public async Task OnMockFinishedAsync(IslandBootstrapDto bootstrap, string mockSessionId, MockSummaryDto summary, CancellationToken ct) =>
        _ = await history.AddAsync(summary.Stack, LocalMockHistoryStore.ToHistory(mockSessionId, summary));

    // A replay nobody will wait for must not leave an unobserved exception behind.
    private static void Observe(Task task) =>
        _ = task.ContinueWith(static finished => _ = finished.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    private TimeSpan Offset => time.GetLocalNow().Offset;

    private Task<ApiResult<StaticIndexDto>> LoadIndexAsync(IslandBootstrapDto bootstrap, CancellationToken ct) =>
        bootstrap.StaticSite is { } site
            ? content.GetIndexAsync(site.IndexPath, ct)
            : Task.FromResult(new ApiResult<StaticIndexDto>(null, 500, ApiErrorCodes.ServerError));

    private async Task<SessionRemainder> SelectRemainderAsync(
        Task<LocalProgress> progressTask,
        IReadOnlyList<PracticeCandidate> candidates,
        IReadOnlySet<QuestionId> live,
        PracticeSelection selection)
    {
        LocalProgress local = await progressTask;
        PracticePick pick = selector.Select(candidates, selection, local.DueAtById(live), time.GetUtcNow());
        return RemainderOf(pick, selection.First!.Value);
    }

    // The plan already holds the opening question, so it is taken out by id rather than by position.
    internal static SessionRemainder RemainderOf(PracticePick pick, QuestionId opening) =>
        new([.. pick.Ids.Where(id => id != opening).Select(id => id.Value)], pick.PoolSize);

    private static async Task<ApiResult<QuestionDto>[]> FetchAllAsync(
        StaticContentClient content, StaticIndexDto index, IReadOnlyList<QuestionId> ids, CancellationToken ct)
    {
        using SemaphoreSlim gate = new(MockFetchParallelism);
        Task<ApiResult<QuestionDto>>[] fetches = [.. ids.Select(id => FetchOneAsync(content, gate, index, id, ct))];
        return await Task.WhenAll(fetches);
    }

    private static async Task<ApiResult<QuestionDto>> FetchOneAsync(
        StaticContentClient content, SemaphoreSlim gate, StaticIndexDto index, QuestionId id, CancellationToken ct)
    {
        StaticIndexEntryDto? entry = StaticIndex.Find(index, id.Value);
        if (entry is null)
        {
            return new ApiResult<QuestionDto>(null, 404, ApiErrorCodes.QuestionNotFound);
        }

        try
        {
            await gate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return new ApiResult<QuestionDto>(null, 0, null);
        }

        try
        {
            return await content.GetQuestionAsync(index.Culture, entry, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private static TopicKey? ParseTopic(string value) => TopicKey.TryCreate(value, out TopicKey key) ? key : null;

    private static PracticeSelection ToSelection(PracticeSessionRequest request)
    {
        List<Level> levels = [.. request.Levels.Select(l => LevelKeys.TryParse(l, out Level level) ? (Level?)level : null).OfType<Level>()];
        List<TopicKey> topics = [.. request.Topics.Select(ParseTopic).OfType<TopicKey>()];
        List<QuestionType> types = [.. request.Types.Select(t => QuestionTypeKeys.TryParse(t, out QuestionType type) ? (QuestionType?)type : null).OfType<QuestionType>()];
        PracticeStatus status = request.Status switch
        {
            PracticeStatusKeys.New => PracticeStatus.New,
            PracticeStatusKeys.Due => PracticeStatus.Due,
            _ => PracticeStatus.All,
        };
        QuestionId? first = QuestionId.TryCreate(request.FirstQuestionId, out QuestionId firstId) ? firstId : null;
        HashSet<QuestionId> exclude =
        [
            .. request.ExcludeQuestionIds
                .Select(id => QuestionId.TryCreate(id, out QuestionId excluded) ? (QuestionId?)excluded : null)
                .OfType<QuestionId>()
                .Take(SessionLimits.MaxExcludedQuestions),
        ];
        return new PracticeSelection(
            levels, topics, types, status, Math.Clamp(request.Count, SessionLimits.MinPracticeCount, SessionLimits.MaxCount), first, exclude);
    }

    private static ApiResult<T> Success<T>(T value) => new(value, 200, null);

    private static ApiResult<T> Failure<T>(ApiResult<StaticIndexDto> failed) => new(default, failed.Status, failed.ErrorCode, failed.RetryAfter);

    private static ApiResult<T> Failure<T>(ApiResult<QuestionDto> failed) => new(default, failed.Status, failed.ErrorCode, failed.RetryAfter);
}
