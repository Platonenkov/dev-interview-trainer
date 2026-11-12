using System.Data.Common;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Trainer.Application.Content;
using Trainer.Application.Questions;
using Trainer.Domain.Keys;
using Trainer.Infrastructure.Persistence;
using Trainer.TestSupport;
using Trainer.Web.Caching;

namespace Trainer.Web.Tests;

public sealed class OutputCacheTests(WebSiteFixture site, PostgresFixture postgres) : IClassFixture<WebSiteFixture>
{
    private const string QuestionPath = "/en/dotnet/questions/closure-for-loop";
    private const string OriginalTitle = "What does the code print when lambdas capture a for loop variable?";
    private const string EditedTitle = "Edited closure title";

    private static async Task<(HttpResponseMessage Response, string Body)> GetAsync(
        HttpClient client, string path, string? cookie = null, string? authorization = null)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return (response, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    // The output cache middleware adds Age only when it serves a stored entry.
    private static bool IsHit(HttpResponseMessage response) => response.Headers.Contains("Age");

    private static async Task ImportAsync(TrainerWebFactory factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        IContentImportService import = scope.ServiceProvider.GetRequiredService<IContentImportService>();
        ContentImportResult result = await import.ImportAsync(factory.Content.Root, TestContext.Current.CancellationToken);
        Assert.Equal(ContentImportStatus.Imported, result.Status);
    }

    private async Task<TrainerWebFactory> CreateOwnFactoryAsync(TrainerWebFactoryOptions? options = null)
    {
        TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres, options);
        await factory.WaitUntilReadyAsync();
        return factory;
    }

    [Fact]
    public async Task AnonymousPublicPage_SecondRequestIsCacheHit()
    {
        using HttpClient client = site.Factory.CreateNonRedirectingClient();

        (HttpResponseMessage first, _) = await GetAsync(client, "/en/about");
        (HttpResponseMessage second, _) = await GetAsync(client, "/en/about");
        using HttpResponseMessage _1 = first;
        using HttpResponseMessage _2 = second;

        Assert.False(IsHit(first));
        Assert.True(IsHit(second));
    }

    [Theory]
    [InlineData("/ru")]
    [InlineData("/ru/about")]
    [InlineData("/ru/dotnet/questions")]
    [InlineData("/ru/dotnet/topics/async")]
    [InlineData("/ru/dotnet/questions/closure-for-loop")]
    [InlineData("/sitemap.xml")]
    [InlineData("/sitemap-ru.xml")]
    [InlineData("/robots.txt")]
    public async Task AnonymousPublicPages_SetNoCookies(string path)
    {
        using HttpClient client = site.Factory.CreateNonRedirectingClient();

        // Twice, so the cache-hit path is covered as well.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            (HttpResponseMessage response, _) = await GetAsync(client, path);
            using HttpResponseMessage _1 = response;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }
    }

    [Fact]
    public async Task RequestWithSessionCookie_BypassesCache()
    {
        using HttpClient client = site.Factory.CreateNonRedirectingClient();
        string cookie = AuthCookieNames.Session + "=x";

        // Warm the shared entry first: a bypassing request must not be served from it either.
        (HttpResponseMessage warm, _) = await GetAsync(client, "/ru/about");
        (HttpResponseMessage warmHit, _) = await GetAsync(client, "/ru/about");
        (HttpResponseMessage first, _) = await GetAsync(client, "/ru/about", cookie);
        (HttpResponseMessage second, _) = await GetAsync(client, "/ru/about", cookie);
        using HttpResponseMessage _1 = warm;
        using HttpResponseMessage _2 = warmHit;
        using HttpResponseMessage _3 = first;
        using HttpResponseMessage _4 = second;

        Assert.True(IsHit(warmHit));
        Assert.False(IsHit(first));
        Assert.False(IsHit(second));
    }

    [Fact]
    public async Task RequestWithAuthorizationHeader_BypassesCache()
    {
        await using TrainerWebFactory factory = await CreateOwnFactoryAsync();
        using HttpClient client = factory.CreateNonRedirectingClient();

        (HttpResponseMessage warm, _) = await GetAsync(client, "/en/about");
        (HttpResponseMessage warmHit, _) = await GetAsync(client, "/en/about");
        (HttpResponseMessage first, _) = await GetAsync(client, "/en/about", authorization: "Bearer x");
        (HttpResponseMessage second, _) = await GetAsync(client, "/en/about", authorization: "Bearer x");
        using HttpResponseMessage _1 = warm;
        using HttpResponseMessage _2 = warmHit;
        using HttpResponseMessage _3 = first;
        using HttpResponseMessage _4 = second;

        Assert.True(IsHit(warmHit));
        Assert.False(IsHit(first));
        Assert.False(IsHit(second));
    }

    [Fact]
    public async Task ResponseForAuthorizationRequest_IsNotStoredForAnonymousVisitors()
    {
        await using TrainerWebFactory factory = await CreateOwnFactoryAsync();
        using HttpClient client = factory.CreateNonRedirectingClient();

        (HttpResponseMessage authorized, _) = await GetAsync(client, "/ru/about", authorization: "Bearer x");
        (HttpResponseMessage anonymous, _) = await GetAsync(client, "/ru/about");
        using HttpResponseMessage _1 = authorized;
        using HttpResponseMessage _2 = anonymous;

        Assert.False(IsHit(anonymous));
    }

    [Fact]
    public async Task EquivalentCatalogQueries_ShareOneCacheEntry()
    {
        using HttpClient client = site.Factory.CreateNonRedirectingClient();

        (HttpResponseMessage first, _) = await GetAsync(client, "/en/dotnet/questions?level=middle");
        (HttpResponseMessage second, _) = await GetAsync(client, "/en/dotnet/questions?level=MIDDLE&level=middle&utm_source=x");
        (HttpResponseMessage other, _) = await GetAsync(client, "/en/dotnet/questions?level=lead");
        using HttpResponseMessage _1 = first;
        using HttpResponseMessage _2 = second;
        using HttpResponseMessage _3 = other;

        Assert.True(IsHit(second));
        Assert.False(IsHit(other));
    }

    [Fact]
    public async Task OtherPages_IgnoreQueryString()
    {
        using HttpClient client = site.Factory.CreateNonRedirectingClient();

        (HttpResponseMessage first, _) = await GetAsync(client, "/en/dotnet/topics/concurrency");
        (HttpResponseMessage second, _) = await GetAsync(client, "/en/dotnet/topics/concurrency?utm_source=x&a=1");
        using HttpResponseMessage _1 = first;
        using HttpResponseMessage _2 = second;

        Assert.True(IsHit(second));
    }

    [Fact]
    public async Task Import_EvictsCachedPages()
    {
        await using TrainerWebFactory factory = await CreateOwnFactoryAsync();
        using HttpClient client = factory.CreateNonRedirectingClient();

        (HttpResponseMessage warm, _) = await GetAsync(client, QuestionPath);
        (HttpResponseMessage cached, string cachedBody) = await GetAsync(client, QuestionPath);
        using HttpResponseMessage _1 = warm;
        using HttpResponseMessage _2 = cached;
        Assert.True(IsHit(cached));
        Assert.Contains(OriginalTitle, cachedBody, StringComparison.Ordinal);

        factory.Content.Edit("dotnet/csharp/closure-for-loop.yaml", OriginalTitle, EditedTitle);
        await ImportAsync(factory);

        (HttpResponseMessage after, string afterBody) = await GetAsync(client, QuestionPath);
        using HttpResponseMessage _3 = after;
        Assert.False(IsHit(after));
        Assert.Contains(EditedTitle, afterBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotFound_IsNotCached()
    {
        using HttpClient client = site.Factory.CreateNonRedirectingClient();

        (HttpResponseMessage first, _) = await GetAsync(client, "/en/dotnet/questions/no-such-question");
        (HttpResponseMessage second, _) = await GetAsync(client, "/en/dotnet/questions/no-such-question");
        (HttpResponseMessage direct, _) = await GetAsync(client, "/not-found");
        using HttpResponseMessage _1 = first;
        using HttpResponseMessage _2 = second;
        using HttpResponseMessage _3 = direct;

        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.False(IsHit(second));
        Assert.False(IsHit(direct));
    }

    [Fact]
    public async Task PermanentRedirect_IsNotCached()
    {
        using HttpClient client = site.Factory.CreateNonRedirectingClient();

        (HttpResponseMessage first, _) = await GetAsync(client, "/ru/dotnet/questions/old-closure");
        (HttpResponseMessage second, _) = await GetAsync(client, "/ru/dotnet/questions/old-closure");
        using HttpResponseMessage _1 = first;
        using HttpResponseMessage _2 = second;

        Assert.Equal(HttpStatusCode.MovedPermanently, second.StatusCode);
        Assert.False(IsHit(second));
    }

    [Fact]
    public async Task CacheHit_ExecutesNoSql()
    {
        SqlCounter counter = new();
        await using TrainerWebFactory factory = await CreateOwnFactoryAsync(new TrainerWebFactoryOptions
        {
            ConfigureServices = services => services.ConfigureDbContext<TrainerDbContext>(options => options.AddInterceptors(counter)),
        });
        using HttpClient client = factory.CreateNonRedirectingClient();

        (HttpResponseMessage miss, _) = await GetAsync(client, QuestionPath);
        using HttpResponseMessage _1 = miss;
        int afterMiss = counter.Count;
        Assert.True(afterMiss > 0, "The counting interceptor did not observe the page's queries.");

        (HttpResponseMessage hit, _) = await GetAsync(client, QuestionPath);
        using HttpResponseMessage _2 = hit;

        Assert.True(IsHit(hit));
        Assert.Equal(afterMiss, counter.Count);
    }

    [Fact]
    public async Task InFlightRenderDuringImport_DoesNotRecacheStaleContent()
    {
        QueryGate gate = new();
        await using TrainerWebFactory factory = await CreateOwnFactoryAsync(new TrainerWebFactoryOptions
        {
            ConfigureServices = services =>
            {
                ServiceDescriptor original = services.Single(d => d.ServiceType == typeof(IQuestionQueries));
                services.Remove(original);
                services.AddSingleton(gate);
                services.AddScoped<IQuestionQueries>(provider => new GatedQuestionQueries(
                    (IQuestionQueries)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!),
                    provider.GetRequiredService<QueryGate>()));
            },
        });
        using HttpClient client = factory.CreateNonRedirectingClient();

        gate.Arm();
        Task<(HttpResponseMessage Response, string Body)> inFlight = GetAsync(client, QuestionPath);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        factory.Content.Edit("dotnet/csharp/closure-for-loop.yaml", OriginalTitle, EditedTitle);
        await ImportAsync(factory);
        gate.Release();

        (HttpResponseMessage stale, string staleBody) = await inFlight;
        using HttpResponseMessage _1 = stale;
        Assert.Contains(OriginalTitle, staleBody, StringComparison.Ordinal);

        (HttpResponseMessage fresh, string freshBody) = await GetAsync(client, QuestionPath);
        using HttpResponseMessage _2 = fresh;

        Assert.False(IsHit(fresh));
        Assert.Contains(EditedTitle, freshBody, StringComparison.Ordinal);
        Assert.DoesNotContain(OriginalTitle, freshBody, StringComparison.Ordinal);
    }

    private sealed class SqlCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _count);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Interlocked.Increment(ref _count);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Interlocked.Increment(ref _count);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class QueryGate
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        public Task Entered => _entered.Task;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Release() => _released.TrySetResult();

        public async Task PassAsync(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 0)
            {
                return;
            }

            _entered.TrySetResult();
            await _released.Task.WaitAsync(ct);
        }
    }

    /// <summary>Reads the data first and only then waits, so the gated request renders the content as it was before the import.</summary>
    private sealed class GatedQuestionQueries(IQuestionQueries inner, QueryGate gate) : IQuestionQueries
    {
        public async Task<QuestionPageModel?> GetAsync(CultureKey culture, StackKey stack, Slug slug, CancellationToken ct)
        {
            QuestionPageModel? model = await inner.GetAsync(culture, stack, slug, ct);
            await gate.PassAsync(ct);
            return model;
        }

        public Task<Slug?> GetRedirectAsync(StackKey stack, Slug from, CancellationToken ct) => inner.GetRedirectAsync(stack, from, ct);
    }
}
