using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Trainer.Application.Users;
using Trainer.Domain.Users;
using Trainer.TestSupport;
using Trainer.Web.Auth;
using Trainer.Web.Caching;
using static Trainer.Web.Tests.SignInTestHelpers;

namespace Trainer.Web.Tests;

/// <summary>
/// One site with its own fake clock, started at the real now (the fake Telegram provider signs tokens with the real
/// clock), and a counter of revalidation reads. A test gets its own site because it moves the clock.
/// </summary>
internal sealed class SessionSite : IAsyncDisposable
{
    private readonly bool _ownsTelegram;

    private SessionSite(FakeTelegramOidcServer telegram, bool ownsTelegram, TrainerWebFactory factory, FakeTimeProvider clock, UserAccountCallCounter calls)
    {
        Telegram = telegram;
        _ownsTelegram = ownsTelegram;
        Factory = factory;
        Clock = clock;
        Calls = calls;
    }

    public FakeTelegramOidcServer Telegram { get; }

    public TrainerWebFactory Factory { get; }

    public FakeTimeProvider Clock { get; }

    public UserAccountCallCounter Calls { get; }

    public static async Task<SessionSite> StartAsync(
        PostgresFixture postgres,
        IReadOnlyDictionary<string, string?>? settings = null,
        string? connectionString = null,
        FakeTelegramOidcServer? telegram = null,
        FakeTimeProvider? clock = null)
    {
        FakeTelegramOidcServer server = telegram ?? await FakeTelegramOidcServer.StartAsync(TrainerWebFactoryOptions.TelegramClientId);
        FakeTimeProvider time = clock ?? new FakeTimeProvider(DateTimeOffset.UtcNow);
        UserAccountCallCounter calls = new();
        TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions
        {
            Telegram = server,
            Time = time,
            Settings = settings,
            ConnectionString = connectionString,
            ConfigureServices = services => CountingUserAccountService.Decorate(services, calls),
        });
        await factory.WaitUntilReadyAsync();
        return new SessionSite(server, telegram is null, factory, time, calls);
    }

    /// <summary>A cookie-keeping browser signed in as <paramref name="who"/>, plus the callback response.</summary>
    public async Task<(HttpClient Client, HttpResponseMessage Callback)> SignInAsync(FakeTelegramIdentity who, CancellationToken ct)
    {
        HttpClient client = Browser(Factory);
        HttpResponseMessage callback = await TelegramSignInDriver.SignInAsync(client, Telegram, who, "/en", ct);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        return (client, callback);
    }

    public async Task<T> WithUsersAsync<T>(Func<IUserAccountService, Task<T>> action)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<IUserAccountService>());
    }

    public async Task<UserAccess> AccessOfAsync(long telegramUserId, CancellationToken ct) =>
        await WithUsersAsync(users => users.GetAccessAsync(telegramUserId, ct)) ?? throw new InvalidOperationException("The user does not exist.");

    /// <summary>
    /// A <c>Cookie</c> header value carrying a ticket the app itself protects, with chosen times, so a test can reach
    /// states (a ticket 20 days old, a last check in the future) that real traffic would need weeks to produce.
    /// </summary>
    public string ForgeSessionCookie(ClaimsPrincipal principal, DateTimeOffset issued, DateTimeOffset expires, DateTimeOffset? lastChecked)
    {
        AuthenticationProperties properties = new() { IsPersistent = true, IssuedUtc = issued, ExpiresUtc = expires };
        if (lastChecked is { } checkedAt)
        {
            properties.Items[TrainerCookieEvents.LastCheckedKey] = checkedAt.ToString("O", CultureInfo.InvariantCulture);
        }

        CookieAuthenticationOptions options = Factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(TelegramAuth.CookieScheme);
        string protectedTicket = options.TicketDataFormat.Protect(new AuthenticationTicket(principal, properties, TelegramAuth.CookieScheme));
        return $"{AuthCookieNames.Session}={protectedTicket}";
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        if (_ownsTelegram)
        {
            await Telegram.DisposeAsync();
        }
    }
}

/// <summary>An output cache store that records how many entries were ever written, so a test can prove nothing was stored.</summary>
internal sealed class CountingOutputCacheStore : IOutputCacheStore
{
    private readonly ConcurrentDictionary<string, (byte[] Value, string[] Tags)> _entries = new(StringComparer.Ordinal);
    private int _writes;

    public int Count => _entries.Count;

    public int Writes => Volatile.Read(ref _writes);

    public ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken)
    {
        foreach (KeyValuePair<string, (byte[] Value, string[] Tags)> entry in _entries)
        {
            if (entry.Value.Tags.Contains(tag, StringComparer.Ordinal))
            {
                _entries.TryRemove(entry.Key, out _);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_entries.TryGetValue(key, out (byte[] Value, string[] Tags) entry) ? entry.Value : null);

    public ValueTask SetAsync(string key, byte[] value, string[]? tags, TimeSpan validFor, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _writes);
        _entries[key] = (value, tags ?? []);
        return ValueTask.CompletedTask;
    }
}

