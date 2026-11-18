using System.Net;
using Trainer.Contracts;
using Trainer.Web.Client.Api;
using Trainer.Web.Client.Backend;

namespace Trainer.Web.Client.Tests;

public sealed class StaticContentClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (StaticSiteFixture Site, QuestionDto Question, StaticIndexEntryDto Entry) OneQuestion()
    {
        QuestionDto question = TestQuestions.Open();
        StaticSiteFixture site = new(question);
        return (site, question, site.Index.Questions[0]);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task Index_IsReturnedAndMemoised()
    {
        (StaticSiteFixture site, _, _) = OneQuestion();
        using StaticContentClient client = new(site.Handler.Client());

        ApiResult<StaticIndexDto> first = await client.GetIndexAsync(StaticSiteFixture.IndexPath, Ct);
        ApiResult<StaticIndexDto> second = await client.GetIndexAsync(StaticSiteFixture.IndexPath, Ct);

        Assert.True(first.IsSuccess);
        Assert.Equal(site.Index.Questions.Count, first.Value!.Questions.Count);
        Assert.Same(first.Value, second.Value);
        Assert.Single(site.IndexRequests);
    }

    [Fact]
    public async Task HashedFile404_IsSiteUpdated()
    {
        (StaticSiteFixture site, _, StaticIndexEntryDto entry) = OneQuestion();
        site.Handler.Respond = _ => FakeHttpHandler.Error(HttpStatusCode.NotFound, ApiErrorCodes.NotFound);
        using StaticContentClient client = new(site.Handler.Client());

        ApiResult<QuestionDto> result = await client.GetQuestionAsync("en", entry, Ct);

        Assert.Equal(404, result.Status);
        Assert.Equal(ApiErrorCodes.SiteUpdated, result.ErrorCode);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task UnknownIndexFormat_IsSiteUpdated_AndNotRemembered()
    {
        (StaticSiteFixture site, _, _) = OneQuestion();
        StaticIndexDto good = site.Index;
        site.Index = good with { Format = 2 };
        using StaticContentClient client = new(site.Handler.Client());

        ApiResult<StaticIndexDto> newer = await client.GetIndexAsync(StaticSiteFixture.IndexPath, Ct);
        site.Index = good;
        ApiResult<StaticIndexDto> after = await client.GetIndexAsync(StaticSiteFixture.IndexPath, Ct);

        Assert.Equal(404, newer.Status);
        Assert.Equal(ApiErrorCodes.SiteUpdated, newer.ErrorCode);
        Assert.True(after.IsSuccess);
        Assert.Equal(2, site.IndexRequests.Count);
    }

    [Fact]
    public async Task NoResponse_IsStatusZero_AndOtherErrorsKeepTheirStatus()
    {
        (StaticSiteFixture site, _, StaticIndexEntryDto entry) = OneQuestion();
        site.Handler.Respond = _ => throw new HttpRequestException("offline");
        using StaticContentClient client = new(site.Handler.Client());
        Assert.Equal(0, (await client.GetQuestionAsync("en", entry, Ct)).Status);

        site.Handler.Respond = _ => FakeHttpHandler.Error(HttpStatusCode.ServiceUnavailable, ApiErrorCodes.ServerError);
        Assert.Equal(503, (await client.GetQuestionAsync("en", entry, Ct)).Status);

        site.Handler.Respond = _ => FakeHttpHandler.Json(HttpStatusCode.OK, "<html>captive portal</html>");
        ApiResult<QuestionDto> portal = await client.GetQuestionAsync("en", entry, Ct);
        Assert.Null(portal.Value);
    }

    [Fact]
    public async Task ConcurrentGetAndPrefetch_FetchTheFileOnce()
    {
        (StaticSiteFixture site, QuestionDto question, StaticIndexEntryDto entry) = OneQuestion();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        site.Handler.Gate = _ => release.Task;
        using StaticContentClient client = new(site.Handler.Client());

        client.Prefetch("en", entry);
        ValueTask<ApiResult<QuestionDto>> foreground = client.GetQuestionAsync("en", entry, Ct);
        ValueTask<ApiResult<QuestionDto>> second = client.GetQuestionAsync("en", entry, Ct);
        release.SetResult();

        Assert.Equal(question.Id, (await foreground).Value!.Id);
        Assert.Equal(question.Id, (await second).Value!.Id);
        Assert.Single(site.QuestionRequests);
    }

    [Fact]
    public async Task SuccessIsRemembered()
    {
        (StaticSiteFixture site, _, StaticIndexEntryDto entry) = OneQuestion();
        using StaticContentClient client = new(site.Handler.Client());

        await client.GetQuestionAsync("en", entry, Ct);
        await client.GetQuestionAsync("en", entry, Ct);

        Assert.Single(site.QuestionRequests);
    }

    [Fact]
    public async Task FailedPrefetch_IsRetriedByTheForegroundLoad()
    {
        (StaticSiteFixture site, QuestionDto question, StaticIndexEntryDto entry) = OneQuestion();
        bool online = false;
        Func<RecordedRequest, HttpResponseMessage?> serve = site.Handler.Respond;
        site.Handler.Respond = request => online ? serve(request) : throw new HttpRequestException("offline");
        using StaticContentClient client = new(site.Handler.Client());

        client.Prefetch("en", entry);
        await UntilAsync(() => site.QuestionRequests.Count == 1);
        online = true;

        // A call that arrives before the failed prefetch has been forgotten joins it and sees the failure; the next one asks again.
        ApiResult<QuestionDto> result = new(null, 0, null);
        for (int i = 0; i < 500 && !result.IsSuccess; i++)
        {
            result = await client.GetQuestionAsync("en", entry, Ct);
            await Task.Yield();
        }

        Assert.Equal(question.Id, result.Value!.Id);
        Assert.Equal(2, site.QuestionRequests.Count);
    }

    [Fact]
    public async Task CancelledWaiter_DoesNotCancelTheOtherCallersSharedFetch()
    {
        (StaticSiteFixture site, QuestionDto question, StaticIndexEntryDto entry) = OneQuestion();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        site.Handler.Gate = _ => release.Task;
        using StaticContentClient client = new(site.Handler.Client());
        using CancellationTokenSource first = new();

        ValueTask<ApiResult<QuestionDto>> cancelled = client.GetQuestionAsync("en", entry, first.Token);
        ValueTask<ApiResult<QuestionDto>> survivor = client.GetQuestionAsync("en", entry, Ct);
        await first.CancelAsync();
        ApiResult<QuestionDto> cancelledResult = await cancelled;

        // The cancelled caller gets a result at once, while the request is still held, and not an exception.
        Assert.Equal(0, cancelledResult.Status);
        Assert.Null(cancelledResult.Value);
        Assert.False(survivor.IsCompleted);

        release.SetResult();
        Assert.Equal(question.Id, (await survivor).Value!.Id);
        Assert.Single(site.QuestionRequests);
    }

    [Fact]
    public async Task AskedAfterDispose_ReturnsNoResponseInsteadOfThrowing()
    {
        (StaticSiteFixture site, _, StaticIndexEntryDto entry) = OneQuestion();
        StaticContentClient client = new(site.Handler.Client());
        client.Dispose();

        Assert.Equal(0, (await client.GetQuestionAsync("en", entry, Ct)).Status);
        Assert.Equal(0, (await client.GetIndexAsync(StaticSiteFixture.IndexPath, Ct)).Status);
        Assert.Empty(site.Handler.Requests);
    }
}
