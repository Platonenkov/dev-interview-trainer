using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Trainer.Application.Answers;
using Trainer.Application.Users;
using Trainer.Domain.Grading;
using Trainer.Domain.Ids;
using Trainer.Domain.Questions;
using Trainer.Domain.Users;
using Trainer.Infrastructure.Persistence;
using Trainer.TestSupport;
using Trainer.Web.Caching;
using static Trainer.Web.Tests.SignInTestHelpers;

namespace Trainer.Web.Tests;

/// <summary>
/// Signed-in visitors see their own progress on the public pages; the shared output cache never learns about it
/// (spec 8.4). Every test owns its site because it moves the clock.
/// </summary>
public sealed class PersonalFragmentsTests(PostgresFixture postgres)
{
    private const long Alice = 560_101L;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Site(
        FakeTelegramOidcServer telegram, TrainerWebFactory factory, FakeTimeProvider clock, CountingOutputCacheStore store) : IAsyncDisposable
    {
        public FakeTelegramOidcServer Telegram { get; } = telegram;

        public TrainerWebFactory Factory { get; } = factory;

        public FakeTimeProvider Clock { get; } = clock;

        public CountingOutputCacheStore Store { get; } = store;

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            await Telegram.DisposeAsync();
        }
    }

    private async Task<Site> StartAsync()
    {
        FakeTelegramOidcServer telegram = await FakeTelegramOidcServer.StartAsync(TrainerWebFactoryOptions.TelegramClientId);
        CountingOutputCacheStore store = new();
        FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
        TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions
        {
            Telegram = telegram,
            Time = clock,
            ConfigureServices = services => services.Replace(ServiceDescriptor.Singleton<IOutputCacheStore>(store)),
        });
        await factory.WaitUntilReadyAsync();
        return new Site(telegram, factory, clock, store);
    }

    private static async Task<(string Cookie, UserId User)> SignInAsync(Site site, long telegramId)
    {
        using HttpClient browser = Browser(site.Factory);
        using HttpResponseMessage callback = await TelegramSignInDriver.SignInAsync(
            browser, site.Telegram, new FakeTelegramIdentity(telegramId, "Alice Aardvark", null), "/ru", Ct);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        await using AsyncServiceScope scope = site.Factory.Services.CreateAsyncScope();
        UserAccess access = await scope.ServiceProvider.GetRequiredService<IUserAccountService>().GetAccessAsync(telegramId, Ct)
            ?? throw new InvalidOperationException("The user was not created.");
        return ($"{AuthCookieNames.Session}={SessionCookieValue(callback)}", access.UserId);
    }

    private static async Task<QuestionId> QuestionIdAsync(Site site, string slug)
    {
        await using TrainerDbContext db = PostgresFixture.CreateContext(site.Factory.ConnectionString);
        string id = await db.Database.SqlQuery<string>($"""SELECT id AS "Value" FROM questions WHERE slug = {slug}""").SingleAsync(Ct);
        return new QuestionId(id);
    }

    private static async Task AnswerAsync(Site site, UserId user, string slug, Grade grade)
    {
        QuestionId question = await QuestionIdAsync(site, slug);
        await using AsyncServiceScope scope = site.Factory.Services.CreateAsyncScope();
        IAnswerRecorder recorder = scope.ServiceProvider.GetRequiredService<IAnswerRecorder>();
        ReportedAnswer answer = new(
            new ClientAnswerId(scope.ServiceProvider.GetRequiredService<UlidGenerator>().Next()),
            question,
            grade,
            false,
            AnswerMode.Practice,
            null,
            site.Clock.GetUtcNow());
        AnswerBatchResult result = await recorder.RecordAsync(user, [answer], null, Ct);
        Assert.Single(result.Accepted);
    }

    private static async Task<(HttpResponseMessage Response, string Body)> GetAsync(Site site, string path, string? cookie = null)
    {
        using HttpClient client = Bare(site.Factory);
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        HttpResponseMessage response = await client.SendAsync(request, Ct);
        return (response, await response.Content.ReadAsStringAsync(Ct));
    }

    private static bool IsHit(HttpResponseMessage response) => response.Headers.Contains("Age");

    private static int Count(string body, string fragment)
    {
        int count = 0;
        for (int at = body.IndexOf(fragment, StringComparison.Ordinal); at >= 0; at = body.IndexOf(fragment, at + fragment.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public async Task SignedInCatalog_ShowsStatuses_AndAnonymousNextRequestDoesNot()
    {
        await using Site site = await StartAsync();
        const string page = "/ru/dotnet/questions";
        (HttpResponseMessage warm, string anonymousBody) = await GetAsync(site, page);
        (HttpResponseMessage cached, _) = await GetAsync(site, page);
        using (warm)
        using (cached)
        {
            Assert.True(IsHit(cached), "The anonymous warm-up should end in a cache hit.");
        }

        (string cookie, UserId user) = await SignInAsync(site, Alice);
        await AnswerAsync(site, user, "closure-for-loop", Grade.Knew);
        site.Clock.Advance(TimeSpan.FromDays(2));
        await AnswerAsync(site, user, "configureawait-false-purpose", Grade.Partial);
        int writes = site.Store.Writes;
        int entries = site.Store.Count;

        (HttpResponseMessage signedIn, string body) = await GetAsync(site, page, cookie);
        using (signedIn)
        {
            Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
            Assert.False(IsHit(signedIn));
            Assert.Contains("no-store", string.Join(", ", signedIn.Headers.GetValues("Cache-Control")), StringComparison.Ordinal);
            Assert.Equal(1, Count(body, "status-chip--due"));
            Assert.Equal(1, Count(body, "status-chip--knew"));
            Assert.Equal(1, Count(body, "status-chip--partial"));
            Assert.Equal(6, Count(body, "status-chip--new"));
            Assert.Contains("Знаю", body, StringComparison.Ordinal);
            Assert.Contains("Частично", body, StringComparison.Ordinal);
        }

        Assert.Equal(writes, site.Store.Writes);
        Assert.Equal(entries, site.Store.Count);

        (HttpResponseMessage anonymous, string anonymousAfter) = await GetAsync(site, page);
        using (anonymous)
        {
            Assert.True(IsHit(anonymous));
            Assert.Equal(anonymousBody, anonymousAfter);
            Assert.DoesNotContain("status-chip", anonymousAfter, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SignedInTopicPage_ShowsStatusesOnRows()
    {
        await using Site site = await StartAsync();
        (string cookie, UserId user) = await SignInAsync(site, Alice);
        await AnswerAsync(site, user, "result-deadlock-ui-thread", Grade.DidntKnow);

        (HttpResponseMessage response, string body) = await GetAsync(site, "/en/dotnet/topics/async", cookie);
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(4, Count(body, "status-chip--new"));
            Assert.Equal(1, Count(body, "status-chip--didnt-know"));
            Assert.Contains("Didn't know", WebUtility.HtmlDecode(body), StringComparison.Ordinal);
            Assert.Contains("in 1 day", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SignedInQuestionPage_ShowsStatusLine()
    {
        await using Site site = await StartAsync();
        const string page = "/ru/dotnet/questions/closure-for-loop";
        (string cookie, UserId user) = await SignInAsync(site, Alice);

        (HttpResponseMessage fresh, string freshBody) = await GetAsync(site, page, cookie);
        using (fresh)
        {
            Assert.Contains("question__status", freshBody, StringComparison.Ordinal);
            Assert.Contains("status-chip--new", freshBody, StringComparison.Ordinal);
        }

        await AnswerAsync(site, user, "closure-for-loop", Grade.Knew);
        (HttpResponseMessage answered, string answeredBody) = await GetAsync(site, page, cookie);
        using (answered)
        {
            Assert.Contains("question__status", answeredBody, StringComparison.Ordinal);
            Assert.Contains("status-chip--knew", answeredBody, StringComparison.Ordinal);
            Assert.Contains("через 1 день", answeredBody, StringComparison.Ordinal);
            Assert.DoesNotContain("status-chip--new", answeredBody, StringComparison.Ordinal);
        }

        (HttpResponseMessage anonymous, string anonymousBody) = await GetAsync(site, page);
        using (anonymous)
        {
            Assert.DoesNotContain("question__status", anonymousBody, StringComparison.Ordinal);
            Assert.DoesNotContain("status-chip", anonymousBody, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SignedInHome_ShowsDueCountOnReviewCard()
    {
        await using Site site = await StartAsync();
        (string cookie, UserId user) = await SignInAsync(site, Alice);
        await AnswerAsync(site, user, "closure-for-loop", Grade.Knew);
        await AnswerAsync(site, user, "configureawait-false-purpose", Grade.DidntKnow);
        await AnswerAsync(site, user, "thread-safe-collections", Grade.Knew);
        site.Clock.Advance(TimeSpan.FromDays(2));

        (HttpResponseMessage ru, string ruBody) = await GetAsync(site, "/ru", cookie);
        (HttpResponseMessage en, string enBody) = await GetAsync(site, "/en/dotnet", cookie);
        using (ru)
        using (en)
        {
            Assert.Contains("class=\"mode-card__due\">Сегодня к повторению: 3<", ruBody, StringComparison.Ordinal);
            Assert.Contains("class=\"mode-card__due\">Due for review: 3<", enBody, StringComparison.Ordinal);
            Assert.Equal(1, Count(ruBody, "mode-card__due"));
        }

        (HttpResponseMessage anonymous, string anonymousBody) = await GetAsync(site, "/ru");
        using (anonymous)
        {
            Assert.DoesNotContain("mode-card__due", anonymousBody, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SignedInPages_NeverStoreOrServePersonalHtmlFromTheSharedCache()
    {
        await using Site site = await StartAsync();
        string[] pages = ["/ru", "/ru/dotnet/questions", "/ru/dotnet/topics/async", "/ru/dotnet/questions/closure-for-loop"];
        Dictionary<string, string> anonymousBefore = new(StringComparer.Ordinal);
        foreach (string page in pages)
        {
            (HttpResponseMessage response, string body) = await GetAsync(site, page);
            using (response)
            {
                anonymousBefore[page] = body;
            }
        }

        (string cookie, UserId user) = await SignInAsync(site, Alice);
        await AnswerAsync(site, user, "closure-for-loop", Grade.Knew);
        int writes = site.Store.Writes;

        foreach (string page in pages)
        {
            for (int round = 0; round < 2; round++)
            {
                (HttpResponseMessage response, string body) = await GetAsync(site, page, cookie);
                using (response)
                {
                    Assert.False(IsHit(response), page);
                    Assert.Contains("Alice Aardvark", body, StringComparison.Ordinal);
                }
            }
        }

        Assert.Equal(writes, site.Store.Writes);

        // A fresh anonymous render, not a cache hit, must still be byte-identical to the one made before anyone signed in.
        await site.Store.EvictByTagAsync(OutputCacheInvalidator.ContentTag, Ct);
        foreach (string page in pages)
        {
            (HttpResponseMessage anonymous, string body) = await GetAsync(site, page);
            using (anonymous)
            {
                Assert.False(IsHit(anonymous), page);
                Assert.Equal(anonymousBefore[page], body);
                Assert.DoesNotContain("Alice Aardvark", body, StringComparison.Ordinal);
                Assert.DoesNotContain("status-chip", body, StringComparison.Ordinal);
                Assert.DoesNotContain("mode-card__due", body, StringComparison.Ordinal);
                Assert.False(anonymous.Headers.Contains("Set-Cookie"), page);
            }
        }
    }
}
