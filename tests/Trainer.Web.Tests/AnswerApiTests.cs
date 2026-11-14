using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Trainer.Contracts;
using Trainer.TestSupport;
using Trainer.Web.Api;
using static Trainer.Web.Tests.ApiTestHelpers;

namespace Trainer.Web.Tests;

/// <summary><c>POST /api/answers/batch</c>: owner checks (ADR 0008), structural validation and JSON errors.</summary>
public sealed class AnswerApiTests(SignInSiteFixture site, PostgresFixture postgres) : IClassFixture<SignInSiteFixture>
{
    private static long _nextTheoryOwner = 621_000L;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<int> AnswersOfAsync(long telegramId) => CountAsync(
        site.Factory,
        $"""SELECT count(*)::int AS "Value" FROM answers a JOIN users u ON u.id = a.user_id WHERE u.telegram_user_id = {telegramId}""",
        Ct);

    [Fact]
    public async Task Batch_OwnerHeaderMismatch_Returns409AndStoresNothing()
    {
        const long Owner = 620_001L;
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, Owner, Ct);
        string token = await GetTokenAsync(client, Ct);
        using HttpRequestMessage request = Write(HttpMethod.Post, "/api/answers/batch", Batch(Answer(ClosureQuestionId)), token, owner: 620_999L);

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        await AssertErrorAsync(response, HttpStatusCode.Conflict, ApiErrorCodes.OwnerMismatch, Ct);
        Assert.Equal(0, await AnswersOfAsync(Owner));
    }

    [Fact]
    public async Task Batch_MissingOwnerHeader_Returns409()
    {
        const long Owner = 620_002L;
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, Owner, Ct);
        string token = await GetTokenAsync(client, Ct);
        using HttpRequestMessage request = Write(HttpMethod.Post, "/api/answers/batch", Batch(Answer(ClosureQuestionId)), token, owner: null);

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        await AssertErrorAsync(response, HttpStatusCode.Conflict, ApiErrorCodes.OwnerMismatch, Ct);
        Assert.Equal(0, await AnswersOfAsync(Owner));
    }

    [Fact]
    public async Task Batch_UtcOffsetHeaderStoredWhenValid()
    {
        const long Owner = 620_003L;
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, Owner, Ct);
        FormattableString offsetSql = $"""SELECT utc_offset_minutes::int AS "Value" FROM users WHERE telegram_user_id = {Owner}""";

        string token = await GetTokenAsync(client, Ct);
        using (HttpRequestMessage valid = Write(HttpMethod.Post, "/api/answers/batch", Batch(Answer(ClosureQuestionId)), token, Owner, offset: "-180"))
        using (HttpResponseMessage response = await client.SendAsync(valid, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(-180, await CountAsync(site.Factory, offsetSql, Ct));

        using (HttpRequestMessage outOfRange = Write(HttpMethod.Post, "/api/answers/batch", Batch(Answer(ConfigureAwaitQuestionId)), token, Owner, offset: "841"))
        using (HttpResponseMessage response = await client.SendAsync(outOfRange, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(-180, await CountAsync(site.Factory, offsetSql, Ct));
    }

    [Fact]
    public async Task Batch_Anonymous_Returns401Json()
    {
        using HttpClient client = SignInTestHelpers.Browser(site.Factory);
        string token = await GetTokenAsync(client, Ct);
        using HttpRequestMessage request = Write(HttpMethod.Post, "/api/answers/batch", Batch(Answer(ClosureQuestionId)), token, owner: 1);

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, ApiErrorCodes.SignInRequired, Ct);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Batch_Over500_Returns400()
    {
        const long Owner = 620_004L;
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, Owner, Ct);
        AnswerDto[] answers = [.. Enumerable.Range(0, 501).Select(_ => Answer(ClosureQuestionId))];

        using HttpResponseMessage response = await PostBatchAsync(client, Owner, Ct, answers);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, ApiErrorCodes.BatchTooLarge, Ct);
        Assert.Equal(0, await AnswersOfAsync(Owner));
    }

    [Fact]
    public async Task Batch_WrongVersion_Returns400Validation()
    {
        const long Owner = 620_005L;
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, Owner, Ct);
        string token = await GetTokenAsync(client, Ct);
        HttpContent body = Json(new AnswerBatch(2, [Answer(ClosureQuestionId)]), TrainerJsonContext.Default.AnswerBatch);
        using HttpRequestMessage request = Write(HttpMethod.Post, "/api/answers/batch", body, token, Owner);

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, ApiErrorCodes.Validation, Ct);
    }

    [Fact]
    public async Task Batch_MixedValidAndInvalid_AcceptsValidRejectsRest()
    {
        const long Owner = 620_006L;
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, Owner, Ct);
        AnswerDto valid = Answer(ClosureQuestionId);
        AnswerDto badGrade = Answer(ConfigureAwaitQuestionId, grade: "excellent");
        AnswerDto unknownQuestion = Answer(NewUlid());
        AnswerDto badId = Answer(ClosureQuestionId, clientAnswerId: "not-a-ulid");

        using HttpResponseMessage response = await PostBatchAsync(client, Owner, Ct, valid, badGrade, unknownQuestion, badId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AnswerBatchResponse body = await ReadAsync(response, TrainerJsonContext.Default.AnswerBatchResponse, Ct);
        Assert.Equal([valid.ClientAnswerId], body.Accepted);
        Assert.Contains(new RejectedAnswerDto(badGrade.ClientAnswerId, "invalid"), body.Rejected);
        Assert.Contains(new RejectedAnswerDto(unknownQuestion.ClientAnswerId, "question-unknown"), body.Rejected);
        Assert.Contains(new RejectedAnswerDto("", "invalid"), body.Rejected);
        Assert.Equal(3, body.Rejected.Count);
        Assert.Equal(1, await AnswersOfAsync(Owner));
    }

    [Fact]
    public async Task Batch_MissingAnsweredAt_RejectedAsInvalid()
    {
        const long Owner = 620_007L;
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, Owner, Ct);
        AnswerDto answer = Answer(ClosureQuestionId) with { AnsweredAt = null };

        using HttpResponseMessage response = await PostBatchAsync(client, Owner, Ct, answer);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AnswerBatchResponse body = await ReadAsync(response, TrainerJsonContext.Default.AnswerBatchResponse, Ct);
        Assert.Empty(body.Accepted);
        Assert.Equal([new RejectedAnswerDto(answer.ClientAnswerId, "invalid")], body.Rejected);
        Assert.Equal(0, await AnswersOfAsync(Owner));
    }

    public static TheoryData<string, int, string?> NullsAndMissingFields()
    {
        string id = "01KC0000000000000000000001";
        string at = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        return new TheoryData<string, int, string?>
        {
            { """{"version":1,"answers":null}""", 400, ApiErrorCodes.Validation },
            { """{"version":1}""", 400, ApiErrorCodes.Validation },
            { "null", 400, ApiErrorCodes.Validation },
            { """{"version":1,"answers":[null]}""", 200, "" },
            { $$"""{"version":1,"answers":[{"clientAnswerId":"{{id}}","questionId":"{{ClosureQuestionId}}","usedHint":false,"mode":"practice","answeredAt":"{{at}}"}]}""", 200, id },
            { $$"""{"version":1,"answers":[{"questionId":"{{ClosureQuestionId}}","grade":"knew","usedHint":false,"mode":"practice","answeredAt":"{{at}}"}]}""", 200, "" },
        };
    }

    [Theory]
    [MemberData(nameof(NullsAndMissingFields))]
    public async Task Batch_NullsAndMissingFields_NeverReturn500(string json, int status, string? rejectedId)
    {
        long owner = Interlocked.Increment(ref _nextTheoryOwner);
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, owner, Ct);
        string token = await GetTokenAsync(client, Ct);
        using HttpRequestMessage request = Write(HttpMethod.Post, "/api/answers/batch", Json(json), token, owner);

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        if (status == 400)
        {
            await AssertErrorAsync(response, HttpStatusCode.BadRequest, rejectedId!, Ct);
            return;
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AnswerBatchResponse body = await ReadAsync(response, TrainerJsonContext.Default.AnswerBatchResponse, Ct);
        Assert.Empty(body.Accepted);
        Assert.Equal([new RejectedAnswerDto(rejectedId!, "invalid")], body.Rejected);
    }

    private static readonly string[] MalformedBodies =
    [
        """{"version":1,"answers":[""",
        """{"version":1,"answers":[{"clientAnswerId":"01KC0000000000000000000002","answeredAt":"x"}]}""",
        """{"version":"one","answers":[]}""",
        """{"version":1,"answers":[{"usedHint":"yes"}]}""",
    ];

    [Theory]
    [InlineData("Testing")]
    [InlineData("Development")]
    public async Task Batch_MalformedJson_Returns400Json(string environment)
    {
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions { Environment = environment });
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost"),
        });
        string cookie = ForgeSessionCookie(factory, 622_001L);

        foreach (string json in MalformedBodies)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, "/api/answers/batch") { Content = Json(json) };
            request.Headers.TryAddWithoutValidation("Cookie", cookie);

            using HttpResponseMessage response = await client.SendAsync(request, Ct);

            await AssertErrorAsync(response, HttpStatusCode.BadRequest, ApiErrorCodes.Validation, Ct);
        }
    }

    [Fact]
    public async Task Batch_BodyOver256KB_Returns413Json()
    {
        const long Owner = 620_008L;
        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, Owner, Ct);
        string token = await GetTokenAsync(client, Ct);
        string padding = new('a', 300 * 1024);
        HttpContent body = new StringContent($$"""{"version":1,"answers":[],"padding":"{{padding}}"}""", Encoding.UTF8, "application/json");
        using HttpRequestMessage request = Write(HttpMethod.Post, "/api/answers/batch", body, token, Owner);

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        await AssertErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, ApiErrorCodes.PayloadTooLarge, Ct);
    }

    [Theory]
    [InlineData("620009", 620_009L)]
    [InlineData("0", null)]
    [InlineData("-5", null)]
    [InlineData("+5", null)]
    [InlineData(" 5", null)]
    [InlineData("5x", null)]
    [InlineData("99999999999999999999", null)]
    [InlineData("", null)]
    public void OwnerHeader_ParsesOnlyOnePositiveId(string value, long? expected)
    {
        Assert.Equal(expected, OwnerCheckFilter.ParseOwner(value));
        Assert.Null(OwnerCheckFilter.ParseOwner(new Microsoft.Extensions.Primitives.StringValues(["1", "1"])));
    }

    [Theory]
    [InlineData("180", (short)180)]
    [InlineData("-840", (short)-840)]
    [InlineData("840", (short)840)]
    [InlineData("841", null)]
    [InlineData("-841", null)]
    [InlineData("-2147483648", null)]
    [InlineData("1.5", null)]
    [InlineData("abc", null)]
    public void UtcOffsetHeader_ParsesOnlyInRange(string value, short? expected) =>
        Assert.Equal(expected, OwnerCheckFilter.ParseUtcOffset(value));
}
