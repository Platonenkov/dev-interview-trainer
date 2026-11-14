using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Trainer.Contracts;
using Trainer.Contracts.Storage;
using Trainer.Web.Client.Api;
using Trainer.Web.Client.Storage;
using Trainer.Web.Client.Sync;

namespace Trainer.Web.Client.Tests;

public sealed class OutboxSyncServiceTests
{
    private const long Owner = 42;
    private const string BatchPath = "/api/answers/batch";
    private const string QuestionId = "01KBZ3Q8N9V2K3M4P5R6S7T8WA";
    private static readonly DateTimeOffset DeviceStart = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly string OwnerKey = StorageKeys.UserOutbox(Owner);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Id(int n) => "01KC" + n.ToString("D22", CultureInfo.InvariantCulture);

    private static AnswerDto Answer(int n, DateTimeOffset? at = null) =>
        new(Id(n), QuestionId, "knew", false, "practice", null, at ?? DeviceStart.AddSeconds(-n));

    private static MockSummaryDto Summary() =>
        new("dotnet", "middle", 1, false, DeviceStart.AddMinutes(-10), DeviceStart, [new MockTopicResultDto("async", 1, 0, 0, 0)]);

    private static bool IsBatch(RecordedRequest request) => string.Equals(request.Path, BatchPath, StringComparison.Ordinal);

    private sealed class Harness
    {
        public Harness()
        {
            Time = new FakeTimeProvider(DeviceStart);
            Store = new FakeBrowserStore();
            Faulty = new FaultyBrowserStore(Store);
            Outbox = new OutboxStore(Faulty);
            Summaries = new SummaryStore(Faulty);
            Handler = new FakeHttpHandler();
            HttpClient http = Handler.Client();
            Clock = new ServerClock(Time, Store);
            Watcher = new AccountWatcher(new FakeJsRuntime());
            Sync = new OutboxSyncService(
                Outbox, Summaries, new TrainerApiClient(http, new AntiforgeryTokenProvider(http), Clock, Time), Time, Watcher);
        }

        public FakeTimeProvider Time { get; }

        public FakeBrowserStore Store { get; }

        /// <summary>The store the sync service's stores use; it can be told to throw.</summary>
        public FaultyBrowserStore Faulty { get; }

        public AccountWatcher Watcher { get; }

        /// <summary>
        /// Completes when the attempt in progress (if any) has finished, including scheduling its retry. The fakes never
        /// leave work running in the background, so after this nothing else can happen until time is advanced.
        /// </summary>
        public Task IdleAsync() => Sync.CurrentAttempt;

        public OutboxStore Outbox { get; }

        public SummaryStore Summaries { get; }

        public FakeHttpHandler Handler { get; }

        public ServerClock Clock { get; }

        public OutboxSyncService Sync { get; }

        public List<RecordedRequest> Batches => [.. Handler.Requests.Where(IsBatch)];

        public async Task AddAsync(int from, int count, string? key = null)
        {
            for (int n = from; n < from + count; n++)
            {
                await Outbox.AddAsync(key ?? OwnerKey, Answer(n));
            }
        }

        public async Task<string[]> StoredIdsAsync(string? key = null) =>
            [.. (await Outbox.ReadAsync(key ?? OwnerKey)).Select(a => a.ClientAnswerId)];
    }

    [Fact]
    public async Task Answer_ClockChangedBeforeFlush_KeepsOriginalTime()
    {
        Harness h = new();
        h.Clock.Observe(DeviceStart.AddMinutes(4), DeviceStart, DeviceStart);
        DateTimeOffset answeredAt = h.Clock.Now();
        await h.Outbox.AddAsync(OwnerKey, Answer(1, answeredAt));
        h.Time.Advance(TimeSpan.FromHours(3));
        h.Handler.Respond = r => IsBatch(r) ? FakeHttpHandler.AcknowledgeAll(r) : null;

        h.Sync.Start(Owner);
        bool flushed = await h.Sync.FlushAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.True(flushed);
        AnswerDto sent = Assert.Single(Assert.Single(h.Batches).Batch().Answers);
        Assert.Equal(answeredAt, sent.AnsweredAt);
        Assert.Equal(DeviceStart.AddMinutes(4).AddSeconds(0.5), sent.AnsweredAt);
    }

    [Fact]
    public async Task Flush_AcceptedAndRejected_LeaveOutbox()
    {
        Harness h = new();
        await h.AddAsync(1, 3);
        // Accepted: 1, rejected: 2; 3 is not mentioned, so it was not acknowledged and stays.
        h.Handler.Respond = r => IsBatch(r)
            ? FakeHttpHandler.Json(
                HttpStatusCode.OK,
                $"{{\"accepted\":[\"{Id(1)}\"],\"rejected\":[{{\"clientAnswerId\":\"{Id(2)}\",\"reason\":\"answered-too-old\"}}]}}")
            : null;

        h.Sync.Start(Owner);
        bool flushed = await h.Sync.FlushAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.False(flushed);
        Assert.Equal([Id(3)], await h.StoredIdsAsync());
        Assert.Equal(1, h.Sync.Pending);

        h.Handler.Respond = r => IsBatch(r) ? FakeHttpHandler.AcknowledgeAll(r) : null;
        Assert.True(await h.Sync.FlushAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Empty(await h.StoredIdsAsync());
        Assert.Equal(0, h.Sync.Pending);
    }

    [Theory]
    [InlineData("400-validation")]
    [InlineData("400-batch-too-large")]
    [InlineData("413")]
    [InlineData("429")]
    [InlineData("500")]
    [InlineData("network")]
    public async Task Flush_NonSuccess_KeepsEntriesAndBacksOff(string outcome)
    {
        Harness h = new();
        await h.AddAsync(1, 3);
        h.Handler.Respond = r => !IsBatch(r) ? null : outcome switch
        {
            "400-validation" => FakeHttpHandler.Error(HttpStatusCode.BadRequest, ApiErrorCodes.Validation),
            "400-batch-too-large" => FakeHttpHandler.Error(HttpStatusCode.BadRequest, ApiErrorCodes.BatchTooLarge),
            "413" => FakeHttpHandler.Error(HttpStatusCode.RequestEntityTooLarge, ApiErrorCodes.PayloadTooLarge),
            "429" => FakeHttpHandler.Error(HttpStatusCode.TooManyRequests, ApiErrorCodes.RateLimited),
            "500" => FakeHttpHandler.Error(HttpStatusCode.InternalServerError, ApiErrorCodes.ServerError),
            _ => throw new HttpRequestException("offline"),
        };

        h.Sync.Start(Owner);
        await h.IdleAsync();
        Assert.Single(h.Batches);
        Assert.Equal([Id(1), Id(2), Id(3)], await h.StoredIdsAsync());

        // 5 s, then 10 s, then 20 s between attempts.
        foreach (int seconds in new[] { 5, 10, 20 })
        {
            int before = h.Batches.Count;
            h.Time.Advance(TimeSpan.FromSeconds(seconds) - TimeSpan.FromMilliseconds(100));
            await h.IdleAsync();
            Assert.Equal(before, h.Batches.Count);

            h.Time.Advance(TimeSpan.FromMilliseconds(100));
            await h.IdleAsync();
            Assert.Equal(before + 1, h.Batches.Count);
            Assert.Equal([Id(1), Id(2), Id(3)], await h.StoredIdsAsync());
        }

        Assert.Equal(3, h.Sync.Pending);
    }

    [Fact]
    public async Task Flush_BackoffIsCappedAtFiveMinutes()
    {
        Harness h = new();
        await h.AddAsync(1, 1);
        h.Handler.Respond = r => IsBatch(r) ? FakeHttpHandler.Error(HttpStatusCode.ServiceUnavailable, ApiErrorCodes.ServerError) : null;
        h.Sync.Start(Owner);
        await h.IdleAsync();
        Assert.Single(h.Batches);

        // 5, 10, 20, 40, 80, 160 s, then 300 s from there on.
        foreach (int seconds in new[] { 5, 10, 20, 40, 80, 160, 300, 300 })
        {
            int before = h.Batches.Count;
            h.Time.Advance(TimeSpan.FromSeconds(seconds));
            await h.IdleAsync();
            Assert.Equal(before + 1, h.Batches.Count);
        }

        int last = h.Batches.Count;
        h.Time.Advance(TimeSpan.FromSeconds(299));
        await h.IdleAsync();
        Assert.Equal(last, h.Batches.Count);
    }

    [Fact]
    public async Task FlushAsync_TriesNowDuringBackOff_RequestFlushWaits()
    {
        Harness h = new();
        await h.AddAsync(1, 1);
        h.Handler.Respond = r => IsBatch(r) ? FakeHttpHandler.Error(HttpStatusCode.InternalServerError, ApiErrorCodes.ServerError) : null;
        h.Sync.Start(Owner);
        await h.IdleAsync();
        Assert.Single(h.Batches);

        h.Sync.RequestFlush();
        await h.IdleAsync();
        Assert.Single(h.Batches);

        h.Handler.Respond = r => IsBatch(r) ? FakeHttpHandler.AcknowledgeAll(r) : null;
        Assert.True(await h.Sync.FlushAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Equal(2, h.Batches.Count);
    }

    [Theory]
    [InlineData(413)]
    [InlineData(400)]
    public async Task Flush_TooLarge_HalvesChunk(int status)
    {
        Harness h = new();
        await h.AddAsync(1, 300);
        h.Handler.Respond = r =>
        {
            if (!IsBatch(r))
            {
                return null;
            }

            if (r.Batch().Answers.Count <= 50)
            {
                return FakeHttpHandler.AcknowledgeAll(r);
            }

            return status == 413
                ? FakeHttpHandler.Error(HttpStatusCode.RequestEntityTooLarge, ApiErrorCodes.PayloadTooLarge)
                : FakeHttpHandler.Error(HttpStatusCode.BadRequest, ApiErrorCodes.BatchTooLarge);
        };

        h.Sync.Start(Owner);
        await h.IdleAsync();
        Assert.Single(h.Batches);
        h.Time.Advance(TimeSpan.FromSeconds(5));
        await h.IdleAsync();
        Assert.Equal(2, h.Batches.Count);
        h.Time.Advance(TimeSpan.FromSeconds(10));
        await h.IdleAsync();
        Assert.Equal(8, h.Batches.Count);

        Assert.Equal([200, 100, 50, 50, 50, 50, 50, 50], h.Batches.Select(b => b.Batch().Answers.Count));
        Assert.Empty(await h.StoredIdsAsync());

        // A clean attempt restores the full chunk, so one bad proxy moment does not slow down the rest of the page.
        h.Handler.Respond = r => IsBatch(r) ? FakeHttpHandler.AcknowledgeAll(r) : null;
        await h.AddAsync(1000, 120);
        Assert.True(await h.Sync.FlushAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Equal(120, h.Batches[^1].Batch().Answers.Count);
    }

    [Fact]
    public async Task Flush_TooLarge_ChunkNeverBelowOne()
    {
        Harness h = new();
        await h.AddAsync(1, 1);
        h.Handler.Respond = r => IsBatch(r) ? FakeHttpHandler.Error(HttpStatusCode.RequestEntityTooLarge, ApiErrorCodes.PayloadTooLarge) : null;

        h.Sync.Start(Owner);
        await h.IdleAsync();
        Assert.Single(h.Batches);
        h.Time.Advance(TimeSpan.FromSeconds(5));
        await h.IdleAsync();
        Assert.Equal(2, h.Batches.Count);

        Assert.All(h.Batches, b => Assert.Single(b.Batch().Answers));
    }

    [Fact]
    public async Task Flush_On400Antiforgery_RefetchesTokenAndRetriesWithSameOwner()
    {
        Harness h = new();
        await h.AddAsync(1, 2);
        int posts = 0;
        h.Handler.Respond = r =>
        {
            if (!IsBatch(r))
            {
                return null;
            }

            return ++posts == 1
                ? FakeHttpHandler.Error(HttpStatusCode.BadRequest, ApiErrorCodes.Antiforgery)
                : FakeHttpHandler.AcknowledgeAll(r);
        };

        h.Sync.Start(Owner);
        Assert.True(await h.Sync.FlushAsync(TimeSpan.FromSeconds(5), Ct));

        Assert.Equal(
            [FakeHttpHandler.AntiforgeryPath, BatchPath, FakeHttpHandler.AntiforgeryPath, BatchPath],
            h.Handler.Requests.Select(r => r.Path));
        Assert.Equal(["token-1", "token-2"], h.Batches.Select(b => b.Header("X-CSRF-TOKEN")));
        Assert.All(h.Batches, b => Assert.Equal("42", b.Header("X-Outbox-Owner")));
        Assert.Empty(await h.StoredIdsAsync());
    }

    [Fact]
    public async Task Flush_On400AntiforgeryTwice_KeepsEntriesAndBacksOff()
    {
        Harness h = new();
        await h.AddAsync(1, 1);
        h.Handler.Respond = r => IsBatch(r) ? FakeHttpHandler.Error(HttpStatusCode.BadRequest, ApiErrorCodes.Antiforgery) : null;

        h.Sync.Start(Owner);
        await h.IdleAsync();
        Assert.Equal(2, h.Batches.Count);
        await h.IdleAsync();

        Assert.Equal(2, h.Batches.Count);
        Assert.Equal([Id(1)], await h.StoredIdsAsync());
        h.Time.Advance(TimeSpan.FromSeconds(5));
        await h.IdleAsync();
        Assert.Equal(4, h.Batches.Count);
    }

    [Fact]
    public async Task Flush_On409OwnerMismatch_KeepsEntriesAndStops()
    {
        Harness h = new();
        await h.AddAsync(1, 2);
        await h.Summaries.AddPendingAsync(Owner, Id(90), Summary());
        h.Handler.Respond = r => IsBatch(r) ? FakeHttpHandler.Error(HttpStatusCode.Conflict, ApiErrorCodes.OwnerMismatch) : null;
        int mismatches = 0;
        int signedOut = 0;
        h.Sync.AccountMismatch += () => mismatches++;
        h.Sync.SignedOut += () => signedOut++;

        h.Sync.Start(Owner);
        bool flushed = await h.Sync.FlushAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.False(flushed);
        Assert.Equal(1, mismatches);
        Assert.Equal(0, signedOut);
        Assert.Single(h.Batches);
        Assert.DoesNotContain(h.Handler.Requests, r => r.Method == HttpMethod.Put);
        Assert.Equal([Id(1), Id(2)], await h.StoredIdsAsync());
        Assert.Single(await h.Summaries.ReadPendingAsync(Owner));

        h.Sync.RequestFlush();
        h.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.False(await h.Sync.FlushAsync(TimeSpan.FromSeconds(5), Ct));
        await h.IdleAsync();
        Assert.Single(h.Batches);
        Assert.Equal(1, mismatches);
    }

    [Fact]
    public async Task Flush_On401_KeepsEntriesAndStops()
    {
        Harness h = new();
        await h.AddAsync(1, 2);
        h.Handler.Respond = r => IsBatch(r) ? FakeHttpHandler.Error(HttpStatusCode.Unauthorized, ApiErrorCodes.SignInRequired) : null;
        int mismatches = 0;
        int signedOut = 0;
        h.Sync.AccountMismatch += () => mismatches++;
        h.Sync.SignedOut += () => signedOut++;

        h.Sync.Start(Owner);
        bool flushed = await h.Sync.FlushAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.False(flushed);
        Assert.Equal(0, mismatches);
        Assert.Equal(1, signedOut);
        Assert.Equal([Id(1), Id(2)], await h.StoredIdsAsync());

        h.Time.Advance(TimeSpan.FromMinutes(10));
        await h.IdleAsync();
        Assert.Single(h.Batches);
    }

    [Fact]
    public async Task Flush_ChunksOf200()
    {
        Harness h = new();
        await h.AddAsync(1, 450);
        h.Handler.Respond = r => IsBatch(r) ? FakeHttpHandler.AcknowledgeAll(r) : null;

        h.Sync.Start(Owner);
        Assert.True(await h.Sync.FlushAsync(TimeSpan.FromSeconds(5), Ct));

        Assert.Equal([200, 200, 50], h.Batches.Select(b => b.Batch().Answers.Count));
        Assert.Equal(450, h.Batches.SelectMany(b => b.Batch().Answers).Select(a => a.ClientAnswerId).Distinct().Count());
        Assert.All(h.Batches, b => Assert.Equal(AnswerBatch.CurrentVersion, b.Batch().Version));
        Assert.Empty(await h.StoredIdsAsync());
    }

    [Fact]
    public async Task Flush_OnlySendsBoundOwnersOutbox()
    {
        Harness h = new();
        await h.AddAsync(1, 1, StorageKeys.AnonymousOutbox);
        await h.AddAsync(2, 1, StorageKeys.UserOutbox(7));
        await h.AddAsync(3, 1);
        await h.Summaries.AddPendingAsync(7, Id(91), Summary());
        h.Handler.Respond = r => IsBatch(r) ? FakeHttpHandler.AcknowledgeAll(r) : null;

        h.Sync.Start(Owner);
        Assert.True(await h.Sync.FlushAsync(TimeSpan.FromSeconds(5), Ct));

        Assert.Equal([Id(3)], Assert.Single(h.Batches).Batch().Answers.Select(a => a.ClientAnswerId));
        Assert.DoesNotContain(h.Handler.Requests, r => r.Method == HttpMethod.Put);
        Assert.Equal([Id(1)], await h.StoredIdsAsync(StorageKeys.AnonymousOutbox));
        Assert.Equal([Id(2)], await h.StoredIdsAsync(StorageKeys.UserOutbox(7)));
        Assert.Single(await h.Summaries.ReadPendingAsync(7));
    }

    [Fact]
    public async Task Flush_SendsOwnerAndUtcOffsetHeaders()
    {
        Harness h = new();
        h.Time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("UTC+05:30", TimeSpan.FromMinutes(330), "UTC+05:30", "UTC+05:30"));
        await h.AddAsync(1, 1);
        h.Handler.Respond = r => IsBatch(r) ? FakeHttpHandler.AcknowledgeAll(r) : null;

        h.Sync.Start(Owner);
        Assert.True(await h.Sync.FlushAsync(TimeSpan.FromSeconds(5), Ct));

        RecordedRequest batch = Assert.Single(h.Batches);
        Assert.Equal(HttpMethod.Post, batch.Method);
        Assert.Equal("42", batch.Header("X-Outbox-Owner"));
        Assert.Equal("330", batch.Header("X-Client-Utc-Offset"));
        Assert.Equal("token-1", batch.Header("X-CSRF-TOKEN"));
    }

    [Fact]
    public async Task Flush_SkipsInvalidEntries()
    {
        Harness h = new();
        string valid = $"{{\"clientAnswerId\":\"{Id(1)}\",\"questionId\":\"{QuestionId}\",\"grade\":\"knew\",\"usedHint\":false,\"mode\":\"practice\",\"answeredAt\":\"2026-11-12T09:00:00+00:00\"}}";
        string invalid = $"{{\"clientAnswerId\":\"{Id(2)}\",\"questionId\":\"{QuestionId}\",\"grade\":\"great\",\"usedHint\":false,\"mode\":\"practice\",\"answeredAt\":\"2026-11-12T09:00:00+00:00\"}}";
        h.Store.SetRaw(OwnerKey, $"{{\"version\":1,\"answers\":[{valid},{invalid}]}}");
        h.Handler.Respond = r => IsBatch(r) ? FakeHttpHandler.AcknowledgeAll(r) : null;

        h.Sync.Start(Owner);
        Assert.True(await h.Sync.FlushAsync(TimeSpan.FromSeconds(5), Ct));

        Assert.Equal([Id(1)], Assert.Single(h.Batches).Batch().Answers.Select(a => a.ClientAnswerId));
        Assert.Contains(Id(2), h.Store.Raw(OwnerKey), StringComparison.Ordinal);
        Assert.DoesNotContain(Id(1), h.Store.Raw(OwnerKey), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Flush_Summary_RemovedOn204_FailedOnNotFoundCode_KeptOnBare404And500()
    {
        Harness h = new();
        string[] ids = [Id(81), Id(82), Id(83), Id(84), Id(85), Id(86)];
        foreach (string id in ids)
        {
            await h.Summaries.AddPendingAsync(Owner, id, Summary());
        }

        h.Handler.Respond = r =>
        {
            if (r.Method != HttpMethod.Put)
            {
                return null;
            }

            string id = r.Path[(r.Path.LastIndexOf('/') + 1)..];
            return Array.IndexOf(ids, id) switch
            {
                0 => new HttpResponseMessage(HttpStatusCode.NoContent),
                1 => FakeHttpHandler.Error(HttpStatusCode.NotFound, ApiErrorCodes.NotFound),
                2 => FakeHttpHandler.Error(HttpStatusCode.BadRequest, ApiErrorCodes.Validation),
                3 => new HttpResponseMessage(HttpStatusCode.NotFound),
                4 => FakeHttpHandler.Error(HttpStatusCode.InternalServerError, ApiErrorCodes.ServerError),
                _ => throw new HttpRequestException("offline"),
            };
        };

        h.Sync.Start(Owner);
        await h.IdleAsync();

        Assert.Equal(6, h.Handler.Requests.Count(r => r.Method == HttpMethod.Put));
        Assert.Equal([Id(84), Id(85), Id(86)], (await h.Summaries.ReadPendingAsync(Owner)).Select(s => s.MockSessionId));
        IReadOnlyList<StoredMockSummary> failed = await h.Summaries.ReadFailedAsync(Owner);
        Assert.Equal([Id(82), Id(83)], failed.Select(s => s.MockSessionId));
        Assert.Equal([ApiErrorCodes.NotFound, ApiErrorCodes.Validation], failed.Select(s => s.Reason));

        // The kept ones are retried after the back-off.
        h.Handler.Respond = r => r.Method == HttpMethod.Put ? new HttpResponseMessage(HttpStatusCode.NoContent) : null;
        h.Time.Advance(TimeSpan.FromSeconds(5));
        await h.IdleAsync();
        Assert.Equal(9, h.Handler.Requests.Count(r => r.Method == HttpMethod.Put));
        Assert.True(await h.Sync.FlushAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Empty(await h.Summaries.ReadPendingAsync(Owner));
    }

    [Fact]
    public async Task Flush_SummaryHeldInMemory_IsSentAndLeavesOn204()
    {
        Harness h = new();
        h.Store.LocksAvailable = false;
        Assert.False((await h.Summaries.AddPendingAsync(Owner, Id(87), Summary())).Persisted);
        h.Handler.Respond = r => r.Method == HttpMethod.Put ? new HttpResponseMessage(HttpStatusCode.NoContent) : null;

        h.Sync.Start(Owner);
        bool flushed = await h.Sync.FlushAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.True(flushed);
        RecordedRequest put = Assert.Single(h.Handler.Requests, r => r.Method == HttpMethod.Put);
        Assert.EndsWith(Id(87), put.Path, StringComparison.Ordinal);
        Assert.Empty(await h.Summaries.ReadPendingAsync(Owner));
    }

    [Fact]
    public async Task Flush_Summary_2xxOtherThan204_KeepsPending()
    {
        Harness h = new();
        await h.Summaries.AddPendingAsync(Owner, Id(81), Summary());
        h.Handler.Respond = r => r.Method == HttpMethod.Put
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Wi-Fi login</html>", Encoding.UTF8, "text/html") }
            : null;

        h.Sync.Start(Owner);
        await h.IdleAsync();

        Assert.Single(h.Handler.Requests, r => r.Method == HttpMethod.Put);
        Assert.Equal([Id(81)], (await h.Summaries.ReadPendingAsync(Owner)).Select(s => s.MockSessionId));
        Assert.Empty(await h.Summaries.ReadFailedAsync(Owner));
    }

    [Fact]
    public async Task Flush_Summary_TokenEndpointAnswersHtml_KeepsPendingAndSendsNothing()
    {
        Harness h = new();
        await h.Summaries.AddPendingAsync(Owner, Id(81), Summary());
        h.Handler.Respond = r => r.Path == FakeHttpHandler.AntiforgeryPath
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Wi-Fi login</html>", Encoding.UTF8, "text/html") }
            : new HttpResponseMessage(HttpStatusCode.NoContent);

        h.Sync.Start(Owner);
        await h.IdleAsync();

        Assert.Empty(h.Handler.Writes);
        Assert.Equal([Id(81)], (await h.Summaries.ReadPendingAsync(Owner)).Select(s => s.MockSessionId));
        Assert.False(await h.Sync.FlushAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task Flush_StoppedMidAttempt_SendsNoFurtherChunkOrSummary()
    {
        Harness h = new();
        await h.AddAsync(1, 450);
        await h.Summaries.AddPendingAsync(Owner, Id(900), Summary());
        h.Handler.Respond = r =>
        {
            if (!IsBatch(r))
            {
                return null;
            }

            h.Sync.Stop();
            return FakeHttpHandler.AcknowledgeAll(r);
        };

        h.Sync.Start(Owner);
        await h.IdleAsync();

        Assert.Single(h.Batches);
        Assert.DoesNotContain(h.Handler.Requests, r => r.Method == HttpMethod.Put);
        Assert.Equal(250, (await h.StoredIdsAsync()).Length);
        h.Time.Advance(TimeSpan.FromMinutes(10));
        await h.IdleAsync();
        Assert.Single(h.Batches);
    }

    [Theory]
    [InlineData(503, false)]
    [InlineData(429, false)]
    [InlineData(0, false)]
    [InlineData(400, true)]
    public async Task Flush_AnswerPhaseFailure_SendsSummariesOnlyWhenServerWasReachable(int status, bool summariesSent)
    {
        Harness h = new();
        await h.AddAsync(1, 1);
        await h.Summaries.AddPendingAsync(Owner, Id(81), Summary());
        h.Handler.Respond = r =>
        {
            if (r.Method == HttpMethod.Put)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (!IsBatch(r))
            {
                return null;
            }

            return status == 0
                ? throw new HttpRequestException("offline")
                : FakeHttpHandler.Error((HttpStatusCode)status, status == 400 ? ApiErrorCodes.Validation : ApiErrorCodes.ServerError);
        };

        h.Sync.Start(Owner);
        await h.IdleAsync();

        Assert.Equal(summariesSent ? 1 : 0, h.Handler.Requests.Count(r => r.Method == HttpMethod.Put));
        Assert.Equal(summariesSent ? 0 : 1, (await h.Summaries.ReadPendingAsync(Owner)).Count);
        Assert.Equal([Id(1)], await h.StoredIdsAsync());
    }

    [Fact]
    public async Task Flush_ExceptionInAttempt_RetriesLaterAndFlushAsyncReturnsFalse()
    {
        Harness h = new();
        await h.AddAsync(1, 1);
        h.Handler.Respond = r => IsBatch(r) ? FakeHttpHandler.AcknowledgeAll(r) : null;
        h.Faulty.Throw = true;

        h.Sync.Start(Owner);
        await h.IdleAsync();
        Assert.False(await h.Sync.FlushAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Empty(h.Batches);

        h.Faulty.Throw = false;
        h.Time.Advance(TimeSpan.FromSeconds(10));
        await h.IdleAsync();
        Assert.Single(h.Batches);
        Assert.Empty(await h.StoredIdsAsync());
    }

    [Fact]
    public async Task Flush_AccountChangedInAnotherTab_StopsSync()
    {
        Harness h = new();
        await h.AddAsync(1, 1);
        h.Handler.Respond = r => IsBatch(r) ? FakeHttpHandler.Error(HttpStatusCode.InternalServerError, ApiErrorCodes.ServerError) : null;
        await h.Watcher.StartAsync("42");
        h.Sync.Start(Owner);
        await h.IdleAsync();
        Assert.Single(h.Batches);

        h.Watcher.OnAccountPublished("anon");
        h.Time.Advance(TimeSpan.FromMinutes(10));
        await h.IdleAsync();

        Assert.True(h.Sync.IsStopped);
        Assert.Single(h.Batches);
        Assert.False(await h.Sync.FlushAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Single(h.Batches);
        Assert.Equal([Id(1)], await h.StoredIdsAsync());
    }

    [Fact]
    public async Task Start_AfterAccountAlreadyChanged_SendsNothing()
    {
        Harness h = new();
        await h.AddAsync(1, 1);
        await h.Watcher.StartAsync("42");
        h.Watcher.OnAccountPublished("7");

        h.Sync.Start(Owner);
        await h.IdleAsync();

        Assert.True(h.Sync.IsStopped);
        Assert.Empty(h.Handler.Requests);
    }

    [Fact]
    public async Task FlushAsync_Timeout_ReturnsFalseWhileAttemptGoesOn()
    {
        Harness h = new();
        await h.AddAsync(1, 1);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Handler.Gate = r => IsBatch(r) ? release.Task : Task.CompletedTask;
        h.Handler.Respond = r => IsBatch(r) ? FakeHttpHandler.AcknowledgeAll(r) : null;
        h.Sync.Start(Owner);

        Task<bool> flush = h.Sync.FlushAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.False(flush.IsCompleted);
        h.Time.Advance(TimeSpan.FromSeconds(5));

        Assert.False(await flush);
        Assert.Equal([Id(1)], await h.StoredIdsAsync());

        release.SetResult();
        await h.IdleAsync();
        Assert.Empty(await h.StoredIdsAsync());
        Assert.Single(h.Batches);
    }

    [Fact]
    public async Task Flush_SummaryAcknowledgedButRemovalFailed_IsRetried()
    {
        Harness h = new();
        await h.Summaries.AddPendingAsync(Owner, Id(81), Summary());
        h.Handler.Respond = r => r.Method == HttpMethod.Put ? new HttpResponseMessage(HttpStatusCode.NoContent) : null;
        h.Store.FailingRemovals.Add(StorageKeys.PendingSummaries(Owner));

        h.Sync.Start(Owner);
        await h.IdleAsync();
        Assert.Single(h.Handler.Requests, r => r.Method == HttpMethod.Put);
        Assert.Single(await h.Summaries.ReadPendingAsync(Owner));

        h.Store.FailingRemovals.Clear();
        h.Time.Advance(TimeSpan.FromSeconds(5));
        await h.IdleAsync();

        Assert.Equal(2, h.Handler.Requests.Count(r => r.Method == HttpMethod.Put));
        Assert.Empty(await h.Summaries.ReadPendingAsync(Owner));
    }

    [Fact]
    public async Task Flush_SummaryRefusedButMoveFailed_IsRetried()
    {
        Harness h = new();
        await h.Summaries.AddPendingAsync(Owner, Id(81), Summary());
        h.Handler.Respond = r => r.Method == HttpMethod.Put ? FakeHttpHandler.Error(HttpStatusCode.NotFound, ApiErrorCodes.NotFound) : null;
        h.Store.FailingRemovals.Add(StorageKeys.PendingSummaries(Owner));

        h.Sync.Start(Owner);
        await h.IdleAsync();

        h.Store.FailingRemovals.Clear();
        h.Time.Advance(TimeSpan.FromSeconds(5));
        await h.IdleAsync();

        Assert.Equal(2, h.Handler.Requests.Count(r => r.Method == HttpMethod.Put));
        Assert.Empty(await h.Summaries.ReadPendingAsync(Owner));
        Assert.Single(await h.Summaries.ReadFailedAsync(Owner));
    }

    [Fact]
    public async Task Flush_StoppedDuringAntiforgeryRetry_DoesNotResend()
    {
        Harness h = new();
        await h.AddAsync(1, 1);
        h.Handler.Respond = r =>
        {
            if (!IsBatch(r))
            {
                return null;
            }

            h.Sync.Stop();
            return FakeHttpHandler.Error(HttpStatusCode.BadRequest, ApiErrorCodes.Antiforgery);
        };

        h.Sync.Start(Owner);
        await h.IdleAsync();

        Assert.Single(h.Batches);
        Assert.Equal(1, h.Handler.TokensIssued);
        Assert.Equal([Id(1)], await h.StoredIdsAsync());
    }

    [Fact]
    public async Task FlushAsync_CallerCancelled_ThrowsOperationCanceled()
    {
        Harness h = new();
        await h.AddAsync(1, 1);
        h.Handler.Respond = r => IsBatch(r) ? FakeHttpHandler.AcknowledgeAll(r) : null;
        h.Sync.Start(Owner);
        await h.IdleAsync();
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Sync.FlushAsync(TimeSpan.FromSeconds(5), cancelled.Token));
    }

    /// <summary>Wraps the fake store; <see cref="Throw"/> makes every call throw, as a broken interop layer would.</summary>
    internal sealed class FaultyBrowserStore(FakeBrowserStore inner) : IBrowserStore
    {
        public bool Throw { get; set; }

        public ValueTask<StoreResult> MutateAsync(StoreOp op) =>
            Throw ? throw new InvalidOperationException("storage broke") : inner.MutateAsync(op);

        public ValueTask<string?> ReadAsync(string key) =>
            Throw ? throw new InvalidOperationException("storage broke") : inner.ReadAsync(key);
    }
}
