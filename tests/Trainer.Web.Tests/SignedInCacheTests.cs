using System.Net;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Trainer.TestSupport;
using Trainer.Web.Caching;
using static Trainer.Web.Tests.SignInTestHelpers;

namespace Trainer.Web.Tests;

/// <summary>Signed-in pages never touch the shared output cache, in either direction.</summary>
public sealed class SignedInCacheTests(PostgresFixture postgres)
{
    private const string Page = "/ru/dotnet/questions";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool IsHit(HttpResponseMessage response) => response.Headers.Contains("Age");

    private static async Task<(HttpResponseMessage Response, string Body)> GetAsync(HttpClient client, string? cookie = null)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, Page);
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        HttpResponseMessage response = await client.SendAsync(request, Ct);
        return (response, await response.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<string> SignInAsync(TrainerWebFactory factory, FakeTelegramOidcServer telegram, long id, string name)
    {
        using HttpClient browser = Browser(factory);
        using HttpResponseMessage callback = await TelegramSignInDriver.SignInAsync(browser, telegram, new FakeTelegramIdentity(id, name, null), "/ru", Ct);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        return $"{AuthCookieNames.Session}={SessionCookieValue(callback)}";
    }

    private async Task<TrainerWebFactory> StartAsync(FakeTelegramOidcServer telegram, CountingOutputCacheStore store, FakeTimeProvider? clock = null)
    {
        TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions
        {
            Telegram = telegram,
            Time = clock,
            ConfigureServices = services => services.Replace(ServiceDescriptor.Singleton<IOutputCacheStore>(store)),
        });
        await factory.WaitUntilReadyAsync();
        return factory;
    }

    [Fact]
    public async Task SignedInCacheSequence_NeverLeaks()
    {
        await using FakeTelegramOidcServer telegram = await FakeTelegramOidcServer.StartAsync(TrainerWebFactoryOptions.TelegramClientId);
        CountingOutputCacheStore store = new();
        await using TrainerWebFactory factory = await StartAsync(telegram, store);
        using HttpClient anonymous = Bare(factory);

        (HttpResponseMessage firstResponse, string firstBody) = await GetAsync(anonymous);
        (HttpResponseMessage secondResponse, _) = await GetAsync(anonymous);
        using (firstResponse)
        using (secondResponse)
        {
            Assert.False(IsHit(firstResponse));
            Assert.True(IsHit(secondResponse), "The anonymous warm-up should end in a cache hit.");
        }

        int entries = store.Count;
        int writes = store.Writes;
        Assert.True(entries > 0);
        string cookieA = await SignInAsync(factory, telegram, 550001, "Alice Aardvark");
        string cookieB = await SignInAsync(factory, telegram, 550002, "Bob Badger");

        foreach ((string cookie, string own, string other) in new[]
        {
            (cookieA, "Alice Aardvark", "Bob Badger"),
            (cookieB, "Bob Badger", "Alice Aardvark"),
        })
        {
            (HttpResponseMessage response, string body) = await GetAsync(anonymous, cookie);
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.False(IsHit(response));
                Assert.Contains(own, body, StringComparison.Ordinal);
                Assert.DoesNotContain(other, body, StringComparison.Ordinal);
                Assert.Contains("status-chip--new", body, StringComparison.Ordinal);
            }
        }

        Assert.Equal(entries, store.Count);
        Assert.Equal(writes, store.Writes);

        (HttpResponseMessage lastResponse, string lastBody) = await GetAsync(anonymous);
        using (lastResponse)
        {
            Assert.True(IsHit(lastResponse));
            Assert.Equal(firstBody, lastBody);
            Assert.DoesNotContain("status-chip", lastBody, StringComparison.Ordinal);
            Assert.DoesNotContain("Alice Aardvark", lastBody, StringComparison.Ordinal);
            Assert.DoesNotContain("Bob Badger", lastBody, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ExpiredSessionCookie_IsTreatedAsAnonymousAndNotStored()
    {
        await using FakeTelegramOidcServer telegram = await FakeTelegramOidcServer.StartAsync(TrainerWebFactoryOptions.TelegramClientId);
        CountingOutputCacheStore store = new();
        FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
        await using TrainerWebFactory factory = await StartAsync(telegram, store, clock);
        string cookie = await SignInAsync(factory, telegram, 550003, "Expired Visitor");
        clock.Advance(TimeSpan.FromDays(31));
        using HttpClient client = Bare(factory);
        int writes = store.Writes;

        (HttpResponseMessage response, string body) = await GetAsync(client, cookie);
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(">Войти</a>", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Expired Visitor", body, StringComparison.Ordinal);
            Assert.False(IsHit(response));
            string cacheControl = response.Headers.TryGetValues("Cache-Control", out IEnumerable<string>? values) ? string.Join(", ", values) : string.Empty;
            Assert.DoesNotContain("no-store", cacheControl, StringComparison.Ordinal);
        }

        Assert.Equal(writes, store.Writes);
    }
}
