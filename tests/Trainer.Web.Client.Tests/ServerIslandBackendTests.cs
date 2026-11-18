using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Trainer.Contracts;
using Trainer.Web.Client.Api;
using Trainer.Web.Client.Backend;
using Trainer.Web.Client.Storage;

namespace Trainer.Web.Client.Tests;

public sealed class ServerIslandBackendTests
{
    private static readonly DateTimeOffset DeviceStart = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (ServerIslandBackend Backend, FakeHttpHandler Handler) Create()
    {
        FakeTimeProvider device = new(DeviceStart);
        FakeHttpHandler handler = new();
        HttpClient http = handler.Client();
        TrainerApiClient api = new(http, new AntiforgeryTokenProvider(http), new ServerClock(device, new FakeBrowserStore()), device);
        return (new ServerIslandBackend(api), handler);
    }

    private static IslandBootstrapDto Bootstrap(MeDto? me = null) =>
        new("en", "dotnet", [], [], [], new PracticeFiltersDto([], [], [], null, null), me);

    private static MeDto Me() => new(42, 5, 14, [new TopicProgressDto("closures", "Closures", 10, 6, 4, 2)]);

    private static HttpResponseMessage Session(params QuestionDto[] questions) =>
        FakeHttpHandler.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new SessionDto(questions, 9, new Dictionary<string, string> { [questions[0].Id] = "like" }), TrainerJsonContext.Default.SessionDto));

    private static PracticeSessionRequest PracticeRequest => new("en", "dotnet", [], [], [], "all", 20, null, []);

    [Fact]
    public async Task ServerBackend_PracticeMakesExactlyOneRequestAndPreloadsEveryQuestion()
    {
        (ServerIslandBackend backend, FakeHttpHandler handler) = Create();
        QuestionDto[] questions = [TestQuestions.Single(), TestQuestions.Open(), TestQuestions.Multi()];
        handler.Respond = _ => Session(questions);

        ApiResult<SessionPlan> result = await backend.PracticeAsync(Bootstrap(), PracticeRequest, Ct);

        SessionPlan plan = Assert.IsType<SessionPlan>(result.Value);
        Assert.Equal([.. questions.Select(q => q.Id)], plan.QuestionIds);
        Assert.Equal(9, plan.PoolSize);
        Assert.Equal("like", plan.MyRatings![questions[0].Id]);
        foreach (QuestionDto question in questions)
        {
            ValueTask<ApiResult<QuestionDto>> lookup = plan.Questions.GetAsync(question.Id, Ct);
            Assert.True(lookup.IsCompletedSuccessfully);
            Assert.Equal(question.Id, (await lookup).Value!.Id);
            plan.Questions.Prefetch(question.Id);
        }

        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/sessions/practice", request.Path);
    }

    [Fact]
    public async Task ServerBackend_ReviewStillFiltersPendingAnswers()
    {
        (ServerIslandBackend backend, FakeHttpHandler handler) = Create();
        QuestionDto answered = TestQuestions.Single();
        QuestionDto due = TestQuestions.Open();
        handler.Respond = _ => Session(answered, due);
        IslandBootstrapDto bootstrap = Bootstrap(Me());
        OutboxStore outbox = new(new FakeBrowserStore());
        await outbox.AddAsync(
            backend.OutboxKey(bootstrap),
            new AnswerDto("01JC0000000000000000000001", answered.Id, "knew", false, "review", null, DeviceStart));

        ApiResult<SessionPlan> result = await backend.ReviewAsync(bootstrap, new ReviewSessionRequest("en", "dotnet", 20), Ct);
        HashSet<string> pending = [.. (await outbox.ReadAsync(backend.OutboxKey(bootstrap))).Select(a => a.QuestionId)];
        SessionPlan filtered = result.Value!.Without(pending);

        Assert.True(backend.FiltersPendingAnswers);
        Assert.Equal([due.Id], filtered.QuestionIds);
        Assert.Equal(2, result.Value.QuestionIds.Count);
        Assert.Equal(result.Value.PoolSize, filtered.PoolSize);
    }

    [Fact]
    public async Task ServerBackend_FailuresKeepStatusCodeAndRetryAfter()
    {
        (ServerIslandBackend backend, FakeHttpHandler handler) = Create();
        handler.Respond = _ =>
        {
            HttpResponseMessage response = FakeHttpHandler.Error(HttpStatusCode.TooManyRequests, ApiErrorCodes.RateLimited);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        };

        ApiResult<SessionPlan> result = await backend.ReviewAsync(Bootstrap(Me()), new ReviewSessionRequest("en", "dotnet", 20), Ct);

        Assert.Null(result.Value);
        Assert.Equal(429, result.Status);
        Assert.Equal(ApiErrorCodes.RateLimited, result.ErrorCode);
        Assert.Equal(TimeSpan.FromSeconds(30), result.RetryAfter);
    }

    [Fact]
    public void ServerBackend_ProgressAndOutboxFollowTheSignedInUser()
    {
        (ServerIslandBackend backend, _) = Create();
        IslandBootstrapDto signedIn = Bootstrap(Me());
        IslandBootstrapDto anonymous = Bootstrap();

        Assert.True(backend.KeepsProgress(signedIn));
        Assert.False(backend.KeepsProgress(anonymous));
        Assert.Equal(StorageKeys.UserOutbox(42), backend.OutboxKey(signedIn));
        Assert.Equal(StorageKeys.AnonymousOutbox, backend.OutboxKey(anonymous));
        Assert.True(backend.SendsAnsweredAsExclude);
    }

    [Fact]
    public async Task ServerBackend_GetProgress_IsTheBootstrapUsersProgress()
    {
        (ServerIslandBackend backend, _) = Create();

        IslandProgress? signedIn = await backend.GetProgressAsync(Bootstrap(Me()), Ct);
        IslandProgress? anonymous = await backend.GetProgressAsync(Bootstrap(), Ct);

        Assert.Equal(5, signedIn!.StreakDays);
        Assert.Equal(14, signedIn.DueNow);
        Assert.Equal("closures", Assert.Single(signedIn.Topics).Topic);
        Assert.Null(anonymous);
    }

    [Fact]
    public async Task ServerBackend_ProfileAndMock_GoThroughTheApi()
    {
        (ServerIslandBackend backend, FakeHttpHandler handler) = Create();
        handler.Respond = request => request.Path.StartsWith("/api/profile", StringComparison.Ordinal)
            ? FakeHttpHandler.Json(
                HttpStatusCode.OK,
                JsonSerializer.Serialize(new ProfileDto(3, 11, 2, null, [], [], []), TrainerJsonContext.Default.ProfileDto))
            : null;

        ApiResult<ProfileDto> profile = await backend.ProfileAsync(Bootstrap(Me()), Ct);
        ApiResult<MockSessionDto> mock = await backend.MockAsync(Bootstrap(Me()), new MockSessionRequest("en", "dotnet", "middle", [], 10), Ct);

        Assert.Equal(3, profile.Value!.StreakDays);
        Assert.Equal("/api/profile?culture=en&stack=dotnet", handler.Requests[0].Path);
        Assert.Equal(404, mock.Status);
        Assert.Equal("/api/sessions/mock", handler.Requests[1].Path);
    }

    [Fact]
    public async Task ServerBackend_OnSessionFinished_DoesNothing()
    {
        (ServerIslandBackend backend, FakeHttpHandler handler) = Create();

        await backend.OnSessionFinishedAsync(Bootstrap(Me()), Ct);

        Assert.Empty(handler.Requests);
    }
}
