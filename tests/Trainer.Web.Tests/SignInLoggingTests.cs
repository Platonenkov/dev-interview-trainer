using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Trainer.Application.Users;
using Trainer.TestSupport;
using Trainer.Web.Auth;
using static Trainer.Web.Tests.SignInTestHelpers;

namespace Trainer.Web.Tests;

public sealed partial class SignInLoggingTests(SignInSiteFixture site, PostgresFixture postgres) : IClassFixture<SignInSiteFixture>
{
    private static readonly string SinkCategory = typeof(SignInLogSink).FullName!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string ProviderLevelKey = $"Logging:{typeof(CapturingLoggerProvider).FullName}:LogLevel:Default";

    [GeneratedRegex("[?&](code|state|nonce)=", RegexOptions.CultureInvariant)]
    private static partial Regex CallbackSecretParameter();

    [Fact]
    public async Task SignInLogs_ContainNoUsernameIpCookieOrToken()
    {
        site.Logs.Clear();
        FakeTelegramIdentity person = new(530001, "Secret Display Qx", "secret_user_qx");
        FakeTelegramIdentity blocked = new(530002, "Hidden Blocked Zw", "hidden_blocked_zw");
        await BlockAsync(blocked);

        using HttpClient client = Browser(site.Factory);
        using HttpResponseMessage success = await TelegramSignInDriver.SignInAsync(client, site.Telegram, person, "/en", Ct);
        string session = SessionCookieValue(success);
        string page = await client.GetStringAsync("/en", Ct);
        string token = AntiforgeryToken(page);
        using FormUrlEncodedContent form = new([new KeyValuePair<string, string>("__RequestVerificationToken", token)]);
        using HttpResponseMessage logout = await client.PostAsync("/signin-telegram/logout", form, Ct);
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);

        using HttpClient deniedClient = Browser(site.Factory);
        using HttpResponseMessage denied = await TelegramSignInDriver.SignInAsync(deniedClient, site.Telegram, blocked, "/en", Ct);
        Assert.Equal("/signin?denied=1", denied.Headers.Location?.OriginalString);

        using HttpClient failedClient = Browser(site.Factory);
        site.Telegram.FailNext = true;
        using HttpResponseMessage failed = await TelegramSignInDriver.SignInAsync(failedClient, site.Telegram, person, "/en", Ct);
        Assert.Equal("/signin?error=1", failed.Headers.Location?.OriginalString);

        string[] forbidden =
        [
            person.Username!, person.Name!, blocked.Username!, blocked.Name!, "127.0.0.1", session, Uri.UnescapeDataString(session), token,
        ];
        AssertNeverLogged(forbidden);

        CapturedLogEntry[] sink = [.. site.Logs.Entries.Where(e => e.Category == SinkCategory)];
        Assert.Equal(3, sink.Length);
        AssertSinkEntry(sink[0], LogLevel.Information, "Success", 530001L, SignInReasons.Ok);
        AssertSinkEntry(sink[1], LogLevel.Warning, "Denied", 530002L, SignInReasons.GateDenied);
        AssertSinkEntry(sink[2], LogLevel.Warning, "Failure", null, SignInReasons.AccessDenied);
        Assert.DoesNotContain(site.Logs.Entries, e => e.Category.StartsWith("TelegramOidc", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SignInLogs_RemoteErrorSentinels_NeverLogged()
    {
        site.Logs.Clear();
        using HttpClient client = Browser(site.Factory);
        site.Telegram.FailNextWith("sentinel-user@x 203.0.113.7 eyJsentinel");

        using HttpResponseMessage failed = await TelegramSignInDriver.SignInAsync(
            client, site.Telegram, new FakeTelegramIdentity(530003, "Remote", "remote"), "/en", Ct);

        Assert.Equal("/signin?error=1", failed.Headers.Location?.OriginalString);
        AssertNeverLogged(["sentinel", "203.0.113.7"]);
        CapturedLogEntry entry = Assert.Single(site.Logs.Entries, e => e.Category == SinkCategory);
        AssertSinkEntry(entry, LogLevel.Warning, "Failure", null, SignInReasons.RemoteFailure);
    }

    [Theory]
    [InlineData("missing id claim", SignInReasons.MissingId)]
    [InlineData("access_denied", SignInReasons.AccessDenied)]
    [InlineData("rejected by gate", SignInReasons.GateDenied)]
    [InlineData("Message contains error: 'server_error', error_description: 'x'", SignInReasons.RemoteFailure)]
    [InlineData(null, SignInReasons.RemoteFailure)]
    public void Reasons_MapOnlyTheLibrarysFixedDetails(string? detail, string expected)
    {
        Telegram.OpenIdConnect.AspNetCore.TelegramSignInAuditEvent e = new(
            Telegram.OpenIdConnect.AspNetCore.TelegramSignInOutcome.Failure, null, "u", "n", null, "1.2.3.4", detail);

        Assert.Equal(expected, SignInReasons.For(e));
    }

    private async Task BlockAsync(FakeTelegramIdentity who)
    {
        await using AsyncServiceScope scope = site.Factory.Services.CreateAsyncScope();
        IUserAccountService users = scope.ServiceProvider.GetRequiredService<IUserAccountService>();
        UserAccess access = await users.SignInAsync(new TelegramProfile(who.Id, who.Username, who.Name), Ct);
        await users.SetBlockedAsync(access.UserId, blocked: true, Ct);
    }

    // A provider-specific level (Logging:Console:LogLevel:Default in a container) outranks every provider-agnostic
    // rule, so a plain AddFilter("TelegramOidc", None) would let the library's username and IP lines through.
    [Fact]
    public async Task SignInLogs_ProviderSpecificLevel_StillDropsLibraryCategory()
    {
        CapturingLoggerProvider logs = new();
        await using FakeTelegramOidcServer telegram = await FakeTelegramOidcServer.StartAsync(TrainerWebFactoryOptions.TelegramClientId);
        await using TrainerWebFactory factory = await CreateVerboseFactoryAsync(telegram, logs, new Dictionary<string, string?>
        {
            [ProviderLevelKey] = "Trace",
        });
        FakeTelegramIdentity person = new(540001, "Verbose Display Kq", "verbose_user_kq");

        using HttpClient client = Browser(factory);
        using HttpResponseMessage success = await TelegramSignInDriver.SignInAsync(client, telegram, person, "/en", Ct);

        Assert.Equal(HttpStatusCode.Redirect, success.StatusCode);
        Assert.Contains(logs.Entries, e => e.Category == SinkCategory);
        Assert.DoesNotContain(logs.Entries, e => e.Category.StartsWith("TelegramOidc", StringComparison.Ordinal));
        AssertNeverLoggedIn(logs.Entries, [person.Username!, person.Name!]);
    }

    // Request and handler diagnostics at Information or below print the callback URL with the authorization code and
    // state, and the challenge redirect with state and nonce; they stay out even when everything is turned up.
    [Fact]
    public async Task SignInLogs_VerboseFrameworkLogging_KeepsCallbackCodeAndStateOut()
    {
        CapturingLoggerProvider logs = new();
        await using FakeTelegramOidcServer telegram = await FakeTelegramOidcServer.StartAsync(TrainerWebFactoryOptions.TelegramClientId);
        await using TrainerWebFactory factory = await CreateVerboseFactoryAsync(telegram, logs, new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Trace",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
            [ProviderLevelKey] = "Trace",
        });

        using HttpClient client = Browser(factory);
        using HttpResponseMessage success = await TelegramSignInDriver.SignInAsync(
            client, telegram, new FakeTelegramIdentity(540002, "Code Watcher", "code_watcher"), "/en", Ct);

        Assert.Equal(HttpStatusCode.Redirect, success.StatusCode);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Debug);
        foreach (CapturedLogEntry entry in logs.Entries)
        {
            foreach (string text in TextsOf(entry))
            {
                Assert.False(CallbackSecretParameter().IsMatch(text), $"{entry.Category} logged a callback parameter: {entry.Message}");
            }
        }
    }

    private async Task<TrainerWebFactory> CreateVerboseFactoryAsync(
        FakeTelegramOidcServer telegram, CapturingLoggerProvider logs, Dictionary<string, string?> settings)
    {
        TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions
        {
            Telegram = telegram,
            Logs = logs,
            Settings = settings,
        });
        await factory.WaitUntilReadyAsync();
        return factory;
    }

    private void AssertNeverLogged(IReadOnlyList<string> secrets) => AssertNeverLoggedIn(site.Logs.Entries, secrets);

    private static List<string> TextsOf(CapturedLogEntry entry)
    {
        List<string> texts = [entry.Message, entry.Exception ?? string.Empty];
        texts.AddRange(entry.State.Values.Select(v => v?.ToString() ?? string.Empty));
        foreach (CapturedLogScope scope in entry.Scopes)
        {
            texts.Add(scope.Text);
            texts.AddRange(scope.Values.Values.Select(v => v?.ToString() ?? string.Empty));
        }

        return texts;
    }

    private static void AssertNeverLoggedIn(IReadOnlyList<CapturedLogEntry> entries, IReadOnlyList<string> secrets)
    {
        foreach (CapturedLogEntry entry in entries)
        {
            List<string> texts = TextsOf(entry);
            foreach (string secret in secrets)
            {
                Assert.False(
                    texts.Any(t => t.Contains(secret, StringComparison.OrdinalIgnoreCase)),
                    $"'{secret}' was logged by {entry.Category}: {entry.Message}");
            }
        }
    }

    private static void AssertSinkEntry(CapturedLogEntry entry, LogLevel level, string outcome, long? telegramUserId, string reason)
    {
        Assert.Equal(level, entry.Level);
        Assert.Equal(outcome, entry.State["Outcome"]?.ToString());
        Assert.Equal(telegramUserId, (long?)entry.State["TelegramUserId"]);
        Assert.Equal(reason, entry.State["Reason"]);
        Assert.Equal("Sign-in {Outcome} for Telegram id {TelegramUserId}: {Reason}", entry.State["{OriginalFormat}"]);
    }
}
