using System.Net;
using Trainer.Contracts;
using Trainer.TestSupport;
using static Trainer.Web.Tests.ApiTestHelpers;

namespace Trainer.Web.Tests;

/// <summary><c>PUT /api/mock-sessions/{id}</c>: idempotent by id, never reveals another user's session.</summary>
public sealed class MockSessionApiTests(SignInSiteFixture site) : IClassFixture<SignInSiteFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MockSummaryDto Summary() => new(
        "dotnet",
        "middle",
        5,
        Timed: true,
        DateTimeOffset.UtcNow.AddMinutes(-20),
        DateTimeOffset.UtcNow.AddMinutes(-1),
        [new MockTopicResultDto("async", 2, 1, 1, 0), new MockTopicResultDto("csharp", 0, 0, 0, 1)]);

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, long owner, string id, MockSummaryDto summary)
    {
        string token = await GetTokenAsync(client, Ct);
        using HttpRequestMessage request = Write(
            HttpMethod.Put, "/api/mock-sessions/" + id, Json(summary, TrainerJsonContext.Default.MockSummaryDto), token, owner);
        return await client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task MockPut_Idempotent()
    {
        const long Owner = 640_001L;
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, Owner, Ct);
        string id = NewUlid();

        using HttpResponseMessage first = await PutAsync(client, Owner, id, Summary());
        using HttpResponseMessage second = await PutAsync(client, Owner, id, Summary());

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal(1, await CountAsync(site.Factory, $"""SELECT count(*)::int AS "Value" FROM mock_sessions WHERE id = {id}""", Ct));
    }

    [Fact]
    public async Task MockPut_OtherUsersId_Returns404Json()
    {
        const long Alice = 640_002L;
        const long Bob = 640_003L;
        using HttpClient alice = await SignInAsync(site.Factory, site.Telegram, Alice, Ct);
        using HttpClient bob = await SignInAsync(site.Factory, site.Telegram, Bob, Ct);
        string id = NewUlid();
        using HttpResponseMessage created = await PutAsync(alice, Alice, id, Summary());
        Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);

        using HttpResponseMessage response = await PutAsync(bob, Bob, id, Summary());

        await AssertErrorAsync(response, HttpStatusCode.NotFound, ApiErrorCodes.NotFound, Ct);
    }

    [Fact]
    public async Task MockPut_OwnerMismatch_Returns409()
    {
        const long Owner = 640_004L;
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, Owner, Ct);
        string id = NewUlid();

        using HttpResponseMessage response = await PutAsync(client, Owner + 1, id, Summary());

        await AssertErrorAsync(response, HttpStatusCode.Conflict, ApiErrorCodes.OwnerMismatch, Ct);
        Assert.Equal(0, await CountAsync(site.Factory, $"""SELECT count(*)::int AS "Value" FROM mock_sessions WHERE id = {id}""", Ct));
    }

    public static TheoryData<string> InvalidSummaries() => ["finished-before-started", "negative-count", "counts-not-question-count", "unknown-topic"];

    [Theory]
    [MemberData(nameof(InvalidSummaries))]
    public async Task MockPut_InvalidSummary_Returns400(string defect)
    {
        const long Owner = 640_005L;
        MockSummaryDto valid = Summary();
        MockSummaryDto summary = defect switch
        {
            "finished-before-started" => valid with { FinishedAt = valid.StartedAt.AddSeconds(-1) },
            "negative-count" => valid with { Topics = [new MockTopicResultDto("async", 3, 1, 2, -1)] },
            "counts-not-question-count" => valid with { QuestionCount = 6 },
            "unknown-topic" => valid with { Topics = [new MockTopicResultDto("no-such-topic", 5, 0, 0, 0)] },
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, Owner, Ct);
        string id = NewUlid();

        using HttpResponseMessage response = await PutAsync(client, Owner, id, summary);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, ApiErrorCodes.Validation, Ct);
        Assert.Equal(0, await CountAsync(site.Factory, $"""SELECT count(*)::int AS "Value" FROM mock_sessions WHERE id = {id}""", Ct));
    }
}
