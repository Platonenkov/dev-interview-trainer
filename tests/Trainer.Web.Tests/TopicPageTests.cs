using System.Net;

namespace Trainer.Web.Tests;

public sealed class TopicPageTests(WebSiteFixture site) : IClassFixture<WebSiteFixture>
{
    private async Task<(HttpResponseMessage Response, string Body)> GetAsync(string path)
    {
        using HttpClient client = site.Factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        return (response, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TopicPage_ShowsDescriptionAndCounts()
    {
        (HttpResponseMessage response, string body) = await GetAsync("/en/dotnet/topics/concurrency");
        using HttpResponseMessage _ = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Threads, the thread pool, locks", body, StringComparison.Ordinal);
        Assert.Contains("href=\"/en/dotnet/questions/parallel-counter-race\"", body, StringComparison.Ordinal);
        Assert.Contains("href=\"/en/dotnet/questions?level=middle&amp;topic=concurrency\"", body, StringComparison.Ordinal);
        Assert.Contains("level-counts__count\">", body, StringComparison.Ordinal);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task TopicPage_InRussian_OmitsEnglishOnlyQuestions()
    {
        (HttpResponseMessage response, string body) = await GetAsync("/ru/dotnet/topics/async");
        using HttpResponseMessage _ = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Задачи, конечный автомат", body, StringComparison.Ordinal);
        Assert.DoesNotContain("en-only-question", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TopicPage_WithoutLiveQuestions_Returns200AndNoindex()
    {
        (HttpResponseMessage response, string body) = await GetAsync("/en/dotnet/topics/generics");
        using HttpResponseMessage _ = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<meta name=\"robots\" content=\"noindex\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TopicPage_WithQuestions_IsIndexable()
    {
        (HttpResponseMessage response, string body) = await GetAsync("/en/dotnet/topics/concurrency");
        using HttpResponseMessage _ = response;

        Assert.DoesNotContain("noindex", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TopicPage_UnknownTopic_Returns404()
    {
        (HttpResponseMessage response, string body) = await GetAsync("/en/dotnet/topics/no-such-topic");
        using HttpResponseMessage _ = response;

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Page not found", body, StringComparison.Ordinal);
    }
}
