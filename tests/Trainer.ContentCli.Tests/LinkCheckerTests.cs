using System.Net;
using Trainer.ContentCli.Links;

namespace Trainer.ContentCli.Tests;

public sealed class LinkCheckerTests : IAsyncLifetime
{
    private static readonly LinkCheckOptions FastOptions = new() { RetryDelay = TimeSpan.FromMilliseconds(20), Timeout = TimeSpan.FromSeconds(5) };

    private LocalLinkServer _server = null!;

    public async ValueTask InitializeAsync() => _server = await LocalLinkServer.StartAsync();

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    private async Task<LinkCheckResult> CheckAsync(string path, LinkCheckOptions? options = null)
    {
        using HttpClient client = LinkCheckHttpClient.Create();
        return await new LinkChecker(client, options ?? FastOptions).CheckAsync(_server.Url(path).ToString(), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("/ok", "Ok")]
    [InlineData("/missing", "Dead")]
    [InlineData("/gone", "Dead")]
    [InlineData("/teapot", "Dead")]
    [InlineData("/unauthorized", "Unknown")]
    [InlineData("/forbidden", "Unknown")]
    [InlineData("/busy", "Unknown")]
    [InlineData("/down", "Unknown")]
    public async Task Classifies_TheFinalStatus(string path, string expected)
    {
        LinkCheckResult result = await CheckAsync(path);

        Assert.Equal(Enum.Parse<LinkStatus>(expected), result.Status);
    }

    [Theory]
    [InlineData("/head-405")]
    [InlineData("/not-implemented")]
    public async Task HeadRejected_FallsBackToGet(string path)
    {
        LinkCheckResult result = await CheckAsync(path);

        Assert.Equal(LinkStatus.Ok, result.Status);
        Assert.Equal(1, _server.Hits("HEAD", path));
        Assert.Equal(1, _server.Hits("GET", path));
    }

    [Fact]
    public async Task HeadRejected_TheGetAnswerDecides()
    {
        LinkCheckResult result = await CheckAsync("/head-405-then-missing");

        Assert.Equal(LinkStatus.Dead, result.Status);
    }

    [Fact]
    public async Task Forbidden_IsTriedWithGetButNotRetried()
    {
        LinkCheckResult result = await CheckAsync("/forbidden");

        Assert.Equal(LinkStatus.Unknown, result.Status);
        Assert.Equal(1, _server.Hits("HEAD", "/forbidden"));
        Assert.Equal(1, _server.Hits("GET", "/forbidden"));
    }

    [Fact]
    public async Task RedirectChain_IsFollowedToTheEnd()
    {
        LinkCheckResult result = await CheckAsync("/hops/5");

        Assert.Equal(LinkStatus.Ok, result.Status);
    }

    [Fact]
    public async Task TooManyRedirects_IsDead()
    {
        LinkCheckResult result = await CheckAsync("/hops/6");

        Assert.Equal(LinkStatus.Dead, result.Status);
        Assert.Contains("redirects", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedirectLoop_IsDead()
    {
        LinkCheckResult result = await CheckAsync("/loop/a");

        Assert.Equal(LinkStatus.Dead, result.Status);
        Assert.Equal("redirect loop", result.Detail);
    }

    [Fact]
    public async Task RedirectToAnotherScheme_IsDead()
    {
        LinkCheckResult result = await CheckAsync("/to-ftp");

        Assert.Equal(LinkStatus.Dead, result.Status);
    }

    [Fact]
    public async Task ServerError_IsRetriedOnceAndThenSucceeds()
    {
        LinkCheckResult result = await CheckAsync("/flaky");

        Assert.Equal(LinkStatus.Ok, result.Status);
        Assert.Equal(2, _server.Hits("HEAD", "/flaky"));
    }

    [Theory]
    [InlineData("/busy")]
    [InlineData("/down")]
    public async Task PersistentThrottlingOrServerError_IsRetriedExactlyOnce(string path)
    {
        LinkCheckResult result = await CheckAsync(path);

        Assert.Equal(LinkStatus.Unknown, result.Status);
        Assert.Equal(2, _server.Hits("HEAD", path));
    }

    [Fact]
    public async Task Timeout_IsUnknownAfterOneRetry()
    {
        LinkCheckResult result = await CheckAsync("/slow", FastOptions with { Timeout = TimeSpan.FromMilliseconds(300) });

        Assert.Equal(LinkStatus.Unknown, result.Status);
        Assert.Equal("timeout", result.Detail);
        Assert.Equal(2, _server.Hits("HEAD", "/slow"));
    }

    [Fact]
    public async Task Requests_CarryTheUserAgentAndNoCredentials()
    {
        await CheckAsync("/ok");

        Dictionary<string, string> headers = Assert.Single(_server.Requests).Headers;
        Assert.Equal("dev-interview-trainer-link-check", headers["User-Agent"]);
        Assert.False(headers.ContainsKey("Cookie"));
        Assert.False(headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task CookieSetOnARedirectHop_IsNotSentToTheNextHop()
    {
        LinkCheckResult result = await CheckAsync("/cookie-start");

        Assert.Equal(LinkStatus.Ok, result.Status);
        Dictionary<string, string> next = Assert.Single(_server.Requests, r => r.Path == "/cookie-next").Headers;
        Assert.False(next.ContainsKey("Cookie"));
    }

    [Fact]
    public async Task CheckAll_RunsEveryUrlAndSortsTheResults()
    {
        using HttpClient client = LinkCheckHttpClient.Create();
        string[] urls = [_server.Url("/missing").ToString(), _server.Url("/ok").ToString(), _server.Url("/gone").ToString()];

        IReadOnlyList<LinkCheckResult> results = await new LinkChecker(client, FastOptions).CheckAllAsync(urls, TestContext.Current.CancellationToken);

        Assert.Equal([.. urls.Order(StringComparer.Ordinal)], results.Select(r => r.Url));
        Assert.Equal(
            [LinkStatus.Ok, LinkStatus.Dead, LinkStatus.Dead],
            results.Select(r => r.Status).Order());
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }

    private sealed class ThrowingHandler(HttpRequestError error) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new HttpRequestException(error, "simulated");
        }
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, "Dead", "DNS failure")]
    [InlineData(HttpRequestError.SecureConnectionError, "Dead", "TLS failure")]
    [InlineData(HttpRequestError.ConnectionError, "Unknown", "network error: ConnectionError")]
    public async Task NetworkFailure_IsClassifiedAfterOneRetry(HttpRequestError error, string expected, string detail)
    {
        ThrowingHandler handler = new(error);
        using HttpClient client = new(handler);

        LinkCheckResult result = await new LinkChecker(client, FastOptions).CheckAsync("https://example.test/page", TestContext.Current.CancellationToken);

        Assert.Equal(Enum.Parse<LinkStatus>(expected), result.Status);
        Assert.Equal(detail, result.Detail);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task RedirectFromHttpsToHttp_IsDead()
    {
        StubHandler handler = new(request => request.RequestUri!.Scheme == Uri.UriSchemeHttps
            ? new HttpResponseMessage(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri("http://example.test/page") } }
            : new HttpResponseMessage(HttpStatusCode.OK));
        using HttpClient client = new(handler);

        LinkCheckResult result = await new LinkChecker(client, FastOptions).CheckAsync("https://example.test/page", TestContext.Current.CancellationToken);

        Assert.Equal(LinkStatus.Dead, result.Status);
        Assert.Contains("https to http", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedException_BecomesUnknownForThatUrlOnly()
    {
        StubHandler handler = new(request => request.RequestUri!.AbsolutePath == "/boom"
            ? throw new InvalidOperationException("boom")
            : new HttpResponseMessage(HttpStatusCode.OK));
        using HttpClient client = new(handler);
        string[] urls = ["https://example.test/a", "https://example.test/boom", "https://example.test/c"];

        IReadOnlyList<LinkCheckResult> results = await new LinkChecker(client, FastOptions).CheckAllAsync(urls, TestContext.Current.CancellationToken);

        Assert.Equal(3, results.Count);
        LinkCheckResult boom = Assert.Single(results, r => r.Url.EndsWith("/boom", StringComparison.Ordinal));
        Assert.Equal(LinkStatus.Unknown, boom.Status);
        Assert.Equal("unexpected InvalidOperationException", boom.Detail);
        Assert.Equal(2, results.Count(r => r.Status == LinkStatus.Ok));
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        using HttpClient client = new(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LinkChecker(client, FastOptions).CheckAsync("https://example.test/page", cts.Token));
    }

    [Fact]
    public async Task NonWebUrl_IsDeadWithoutARequest()
    {
        StubHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using HttpClient client = new(handler);

        LinkCheckResult result = await new LinkChecker(client, FastOptions).CheckAsync("mailto:a@example.test", TestContext.Current.CancellationToken);

        Assert.Equal(LinkStatus.Dead, result.Status);
        Assert.Equal(0, handler.Calls);
    }
}
