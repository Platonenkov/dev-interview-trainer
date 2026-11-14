using System.Net;
using Microsoft.AspNetCore.Http;
using Trainer.TestSupport;
using Trainer.Web.Catalog;

namespace Trainer.Web.Tests;

/// <summary>ADR 0011: uncached search requests are limited per client address; cached catalog pages never are.</summary>
public sealed class CatalogSearchRateLimitTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const int Limit = 3;

    private TrainerWebFactory _factory = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = await TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions
        {
            Settings = new Dictionary<string, string?> { ["App:RateLimits:SearchPerMinute"] = Limit.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        });
        await _factory.WaitUntilReadyAsync();
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path) => await client.GetAsync(path, Ct);

    private static async Task ExhaustAsync(HttpClient client, string path)
    {
        for (int i = 0; i < Limit; i++)
        {
            using HttpResponseMessage allowed = await GetAsync(client, path);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }
    }

    [Theory]
    [InlineData("/en/dotnet/questions?q=lambdas", "Too many searches", "Back to the catalog")]
    [InlineData("/ru/dotnet/questions?q=lambdas", "Слишком много поисковых запросов", "Назад в каталог")]
    public async Task Search_OverTheLimit_Returns429WithLocalisedNoindexHtml(string path, string title, string back)
    {
        using HttpClient client = _factory.CreateNonRedirectingClient();
        await ExhaustAsync(client, path);

        using HttpResponseMessage limited = await GetAsync(client, path);
        string body = await limited.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("text/html", limited.Content.Headers.ContentType?.MediaType);
        int retryAfter = (int)Assert.NotNull(limited.Headers.RetryAfter?.Delta).TotalSeconds;
        Assert.InRange(retryAfter, 1, 60);
        Assert.Contains("<meta name=\"robots\" content=\"noindex\"", body, StringComparison.Ordinal);
        Assert.Contains("noindex", limited.Headers.GetValues("X-Robots-Tag").Single(), StringComparison.Ordinal);
        Assert.Contains("no-store", limited.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Contains(title, body, StringComparison.Ordinal);
        Assert.Contains(back, body, StringComparison.Ordinal);
        Assert.Contains($"<html lang=\"{path[1..3]}\"", body, StringComparison.Ordinal);
        Assert.Contains($"href=\"{path[..path.IndexOf('?', StringComparison.Ordinal)]}\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("{", body, StringComparison.Ordinal);
        Assert.False(limited.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Search_Limit_DoesNotTouchCachedCatalogOrOtherPages()
    {
        using HttpClient client = _factory.CreateNonRedirectingClient();
        await ExhaustAsync(client, "/en/dotnet/questions?q=one");
        using HttpResponseMessage limited = await GetAsync(client, "/en/dotnet/questions?q=one");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        // Well past the limit: the catalog without a term (blank included), the home page and a question page all still answer.
        for (int i = 0; i < Limit * 2; i++)
        {
            foreach (string path in new[] { "/en/dotnet/questions", "/en/dotnet/questions?level=junior&q=%20", "/en", "/en/dotnet/questions/closure-for-loop" })
            {
                using HttpResponseMessage response = await GetAsync(client, path);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }
    }

    [Fact]
    public async Task Search_Limit_CountsEveryTermTogether()
    {
        using HttpClient client = _factory.CreateNonRedirectingClient();

        for (int i = 0; i < Limit; i++)
        {
            using HttpResponseMessage allowed = await GetAsync(client, $"/en/dotnet/questions?q=term{i}");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using HttpResponseMessage limited = await GetAsync(client, "/ru/dotnet/questions?q=another");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public void Partition_IsTheClientAddress_NotTheUser()
    {
        DefaultHttpContext first = new();
        first.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
        DefaultHttpContext second = new();
        second.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.8");
        DefaultHttpContext unknown = new();

        Assert.Equal("search:ip:203.0.113.7", CatalogSearchRateLimit.Partition(first));
        Assert.NotEqual(CatalogSearchRateLimit.Partition(first), CatalogSearchRateLimit.Partition(second));
        Assert.Equal(CatalogSearchRateLimit.FallbackPartition, CatalogSearchRateLimit.Partition(unknown));
    }

    [Theory]
    [InlineData("GET", "/en/dotnet/questions", "?q=abc", true)]
    [InlineData("HEAD", "/ru/dotnet/questions/", "?q=abc", true)]
    [InlineData("GET", "/en/dotnet/questions", "?q=%20", false)]
    [InlineData("GET", "/en/dotnet/questions", "", false)]
    [InlineData("GET", "/en/dotnet/questions", "?level=junior", false)]
    [InlineData("GET", "/en/dotnet/practice", "?q=abc", false)]
    [InlineData("GET", "/en/dotnet/questions/closure-for-loop", "?q=abc", false)]
    [InlineData("POST", "/en/dotnet/questions", "?q=abc", false)]
    [InlineData("GET", "/api/profile", "?q=abc", false)]
    public void IsSearchRequest_OnlyForCatalogGetsWithATerm(string method, string path, string query, bool expected)
    {
        DefaultHttpContext context = new();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);

        Assert.Equal(expected, CatalogSearchRateLimit.IsSearchRequest(context));
    }
}
