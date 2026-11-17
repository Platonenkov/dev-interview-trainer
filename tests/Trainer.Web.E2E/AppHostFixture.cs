using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using Trainer.Application.Answers;
using Trainer.Application.Feedback;
using Trainer.Application.Users;
using Trainer.Domain.Feedback;
using Trainer.Domain.Grading;
using Trainer.Domain.Ids;
using Trainer.Domain.Keys;
using Trainer.Domain.Questions;
using Trainer.Infrastructure.Persistence;
using Trainer.TestSupport;
using Trainer.Web.Auth;

namespace Trainer.Web.E2E;

/// <summary>
/// The real site on a Kestrel port (a browser cannot reach the in-memory test server), wired to a fake Telegram
/// provider, and the run's shared headless Chromium (<see cref="BrowserFixture"/>). Run <c>playwright.ps1 install
/// chromium</c> from the build output once per machine before the first run.
/// </summary>
public sealed class AppHostFixture(PostgresFixture postgres, BrowserFixture browser) : IAsyncLifetime
{
    /// <summary>The Telegram id the host treats as an admin (<c>App:AdminTelegramIds</c>).</summary>
    public const long AdminTelegramId = 990_000_001L;

    private static readonly TimeSpan SignInTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<IPage, PageWatch> _watches = new();
    private TrainerWebFactory? _factory;
    private FakeTelegramOidcServer? _telegram;
    private IReadOnlyDictionary<string, string?> _settings = new Dictionary<string, string?>();

    /// <summary>A host with extra app settings (for example a lowered rate limit), for fixtures that own their host.</summary>
    internal AppHostFixture(PostgresFixture postgres, BrowserFixture browser, IReadOnlyDictionary<string, string?> settings)
        : this(postgres, browser) => _settings = settings;

    public IBrowser Browser => browser.Browser;

    /// <summary>
    /// <c>http://localhost:{port}</c>: Chromium treats localhost as a secure context, so it keeps the app's
    /// <c>Secure</c> cookies over plain http and a real sign-in works in the browser.
    /// </summary>
    public Uri BaseAddress { get; private set; } = null!;

    /// <summary>The host's services, for seeding data and issuing a session the way the app itself would.</summary>
    public IServiceProvider Services => _factory?.Services ?? throw new InvalidOperationException("The host is not started.");

    /// <summary>The host's database, for checking what reached the server.</summary>
    public string ConnectionString => _factory?.ConnectionString ?? throw new InvalidOperationException("The host is not started.");

    public async ValueTask InitializeAsync()
    {
        try
        {
            _telegram = await FakeTelegramOidcServer.StartAsync(TrainerWebFactoryOptions.TelegramClientId);
            _factory = await TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions
            {
                Telegram = _telegram,
                Settings = new Dictionary<string, string?>(_settings) { ["App:AdminTelegramIds"] = AdminTelegramId.ToString(CultureInfo.InvariantCulture) },
            });
            _factory.UseKestrel(0);
            _factory.StartServer();
            BaseAddress = new UriBuilder(_factory.ClientOptions.BaseAddress) { Host = "localhost" }.Uri;
            await _factory.WaitUntilReadyAsync();
        }
        catch
        {
            // xUnit does not call DisposeAsync when InitializeAsync throws, so the running hosts would leak.
            await DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Signs <paramref name="who"/> in through the real Telegram OIDC flow in a tab of <paramref name="context"/>, so
    /// the context ends up with the session cookie the app issues.
    /// </summary>
    public async Task SignInAsync(IBrowserContext context, FakeTelegramIdentity who)
    {
        ArgumentNullException.ThrowIfNull(context);
        IPage tab = await context.NewPageAsync();
        try
        {
            await SignInAsync(tab, who);
        }
        finally
        {
            await tab.CloseAsync();
        }
    }

    /// <summary>
    /// Signs <paramref name="who"/> in through the real Telegram OIDC flow in <paramref name="tab"/>, which ends on the
    /// signed-in public page <c>/en/about</c> (it publishes the new owner to other tabs).
    /// </summary>
    public async Task SignInAsync(IPage tab, FakeTelegramIdentity who)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(who);
        FakeTelegramOidcServer telegram = _telegram ?? throw new InvalidOperationException("The host is not started.");

        telegram.NextIdentity = who;
        const string returnPath = "/en/about";
        await tab.GotoAsync(new Uri(BaseAddress, "/signin-telegram/challenge?returnUrl=" + Uri.EscapeDataString(returnPath)).ToString());
        await tab.WaitForURLAsync(
            new Uri(BaseAddress, returnPath).ToString(),
            new PageWaitForURLOptions { Timeout = (float)SignInTimeout.TotalMilliseconds });
    }

    /// <summary>
    /// The session cookie value the app itself would issue to the admin (<see cref="AdminTelegramId"/>) after a Telegram
    /// sign-in, so admin pages can be opened without driving the fake provider.
    /// </summary>
    public async Task<string> IssueAdminSessionAsync()
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        UserAccess access = await scope.ServiceProvider.GetRequiredService<IUserAccountService>()
            .SignInAsync(new TelegramProfile(AdminTelegramId, "e2e_admin", "E2E Admin"), CancellationToken.None);
        List<Claim> claims =
        [
            .. new UserClaimsFactory().Create(access),
            new Claim(TrainerClaimTypes.TelegramUserId, access.TelegramUserId.ToString(CultureInfo.InvariantCulture)),
            new Claim(TrainerClaimTypes.DisplayName, "E2E Admin"),
        ];
        ClaimsPrincipal principal = new(new ClaimsIdentity(claims, TelegramAuth.CookieScheme, TrainerClaimTypes.DisplayName, ClaimTypes.Role));
        DateTimeOffset now = Services.GetRequiredService<TimeProvider>().GetUtcNow();
        AuthenticationProperties properties = new() { IsPersistent = true, IssuedUtc = now, ExpiresUtc = now.AddDays(30) };
        CookieAuthenticationOptions options = Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(TelegramAuth.CookieScheme);
        return options.TicketDataFormat.Protect(new AuthenticationTicket(principal, properties, TelegramAuth.CookieScheme));
    }

    /// <summary>
    /// Records answers for a signed-in user through the app's own <see cref="IAnswerRecorder"/>, as if its client had sent
    /// them <c>Ago</c> before now, so their review states (due dates, streak days) exist.
    /// </summary>
    public async Task RecordAnswersAsync(long telegramUserId, params (string QuestionId, Grade Grade, TimeSpan Ago)[] answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        IServiceProvider services = scope.ServiceProvider;
        UserAccess user = await services.GetRequiredService<IUserAccountService>().GetAccessAsync(telegramUserId, CancellationToken.None)
            ?? throw new InvalidOperationException("The user was not created.");
        UlidGenerator ids = services.GetRequiredService<UlidGenerator>();
        DateTimeOffset now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        ReportedAnswer[] reported =
        [
            .. answers.Select(a => new ReportedAnswer(
                new ClientAnswerId(ids.Next()), new QuestionId(a.QuestionId), a.Grade, false, AnswerMode.Practice, null, now - a.Ago)),
        ];
        AnswerBatchResult result = await services.GetRequiredService<IAnswerRecorder>().RecordAsync(user.UserId, reported, null, CancellationToken.None);
        Assert.Equal(answers.Length, result.Accepted.Count);
    }

    /// <summary>
    /// Files a report, and optionally a dislike, as <paramref name="telegramUserId"/> through the app's own services, so
    /// the admin page has rows to show. Repeating a call edits the open report instead of adding one.
    /// </summary>
    public async Task SeedFeedbackAsync(
        long telegramUserId, string displayName, string questionId, string culture, ReportReason reason, string? comment, bool dislike)
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        IServiceProvider services = scope.ServiceProvider;
        UserAccess user = await services.GetRequiredService<IUserAccountService>()
            .SignInAsync(new TelegramProfile(telegramUserId, "seed_" + telegramUserId, displayName), CancellationToken.None);
        IQuestionFeedbackService feedback = services.GetRequiredService<IQuestionFeedbackService>();
        ReportResult report = await feedback.ReportAsync(user.UserId, new QuestionId(questionId), new CultureKey(culture), reason, comment, CancellationToken.None);
        Assert.True(report.Outcome is ReportOutcome.Created or ReportOutcome.Updated, $"The report was not stored: {report.Outcome}.");
        if (dislike)
        {
            await feedback.SetRatingAsync(user.UserId, new QuestionId(questionId), QuestionRating.Dislike, CancellationToken.None);
        }
    }

    /// <summary>How many answer rows the server holds for <paramref name="telegramUserId"/>.</summary>
    public async Task<int> ServerAnswerCountAsync(long telegramUserId)
    {
        await using TrainerDbContext db = PostgresFixture.CreateContext(ConnectionString);
        return await db.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM answers a JOIN users u ON u.id = a.user_id WHERE u.telegram_user_id = {telegramUserId}")
            .SingleAsync();
    }

    /// <summary>Waits up to 30 seconds for the server to hold exactly <paramref name="expected"/> answer rows.</summary>
    public async Task WaitForServerAnswersAsync(long telegramUserId, int expected)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        int last;
        while ((last = await ServerAnswerCountAsync(telegramUserId)) != expected)
        {
            Assert.True(DateTime.UtcNow < deadline, $"Expected {expected} answer rows for {telegramUserId}, found {last}.");
            await Task.Delay(250);
        }
    }

    /// <summary>
    /// A new tab that records every CSP violation of every document it shows and counts its <c>/api/sessions/</c>
    /// requests; check them with <see cref="AssertNoCspViolations"/> and <see cref="SessionRequestCount"/>.
    /// </summary>
    public async Task<IPage> NewPageAsync(IBrowserContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IPage tab = await context.NewPageAsync();
        PageWatch watch = new();
        _watches[tab] = watch;
        tab.Request += (_, request) =>
        {
            if (new Uri(request.Url).AbsolutePath.StartsWith("/api/sessions/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref watch.SessionRequests);
            }
        };

        // An exposed function survives navigations, unlike page globals, so violations of earlier documents are kept.
        await tab.ExposeFunctionAsync("__ditReportCspViolation", (string violation) => watch.Violations.Enqueue(violation));
        await tab.AddInitScriptAsync(
            """
            document.addEventListener('securitypolicyviolation', function (e) {
              window.__ditReportCspViolation(location.pathname + ': ' + e.violatedDirective + ' blocked ' + (e.blockedURI || 'inline') + ' at ' + e.sourceFile + ':' + e.lineNumber);
            });
            """);
        return tab;
    }

    /// <summary>Fails when any document shown in <paramref name="page"/> reported a CSP violation.</summary>
    public void AssertNoCspViolations(IPage page)
    {
        string[] violations = [.. Watch(page).Violations];
        Assert.True(violations.Length == 0, "CSP violations: " + string.Join(" | ", violations));
    }

    /// <summary>How many <c>/api/sessions/</c> requests <paramref name="page"/> has made so far.</summary>
    public int SessionRequestCount(IPage page) => Volatile.Read(ref Watch(page).SessionRequests);

    private PageWatch Watch(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return _watches.TryGetValue(page, out PageWatch? watch)
            ? watch
            : throw new InvalidOperationException("The page was not created with NewPageAsync.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_telegram is not null)
        {
            await _telegram.DisposeAsync();
        }
    }

    private sealed class PageWatch
    {
        public ConcurrentQueue<string> Violations { get; } = new();

        public int SessionRequests;
    }
}
