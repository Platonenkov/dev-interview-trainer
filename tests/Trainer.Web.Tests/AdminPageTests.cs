using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;
using Trainer.Application.Admin;
using Trainer.Application.Feedback;
using Trainer.Application.Users;
using Trainer.Domain.Feedback;
using Trainer.Domain.Keys;
using Trainer.Domain.Questions;
using Trainer.Infrastructure.Persistence;
using Trainer.TestSupport;
using static Trainer.Web.Tests.SignInTestHelpers;

namespace Trainer.Web.Tests;

public sealed partial class AdminPageTests(SignInSiteFixture site, PostgresFixture postgres) : IClassFixture<SignInSiteFixture>
{
    private const long SecondAdminTelegramId = 777001;

    private static long _nextId = 910_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("<form\\b[^>]*method=\"post\"[^>]*>(?<body>.*?)</form>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex PostForm();

    [GeneratedRegex("<input\\b(?<attrs>[^>]*)>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Input();

    [GeneratedRegex("(?<name>[a-z-]+)=\"(?<value>[^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex Attribute();

    private static long NextTelegramId() => Interlocked.Increment(ref _nextId);

    private async Task<T> WithUsersAsync<T>(Func<IUserAccountService, Task<T>> action)
    {
        await using AsyncServiceScope scope = site.Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<IUserAccountService>());
    }

    private Task<UserAccess> CreateUserAsync(long telegramUserId, string displayName) =>
        WithUsersAsync(users => users.SignInAsync(new TelegramProfile(telegramUserId, "u" + telegramUserId, displayName), Ct));

    private async Task<HttpClient> SignedInAsync(long telegramUserId, string name)
    {
        HttpClient client = Browser(site.Factory);
        using HttpResponseMessage callback = await TelegramSignInDriver.SignInAsync(
            client, site.Telegram, new FakeTelegramIdentity(telegramUserId, name, null), "/ru", Ct);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        return client;
    }

    private static async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> GetAsync(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.GetAsync(path, Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct), response);
    }

    /// <summary>The hidden fields of the post form that carries <paramref name="marker"/> (a user id), as a browser would submit them.</summary>
    private static Dictionary<string, string> FormFieldsFor(string html, string marker)
    {
        foreach (Match form in PostForm().Matches(html))
        {
            string body = form.Groups["body"].Value;
            if (!body.Contains(marker, StringComparison.Ordinal))
            {
                continue;
            }

            Dictionary<string, string> fields = new(StringComparer.Ordinal);
            foreach (Match input in Input().Matches(body))
            {
                Dictionary<string, string> attributes = Attribute().Matches(input.Groups["attrs"].Value)
                    .ToDictionary(m => m.Groups["name"].Value, m => WebUtility.HtmlDecode(m.Groups["value"].Value), StringComparer.Ordinal);
                if (attributes.TryGetValue("name", out string? name) && attributes.TryGetValue("value", out string? value))
                {
                    fields[name] = value;
                }
            }

            return fields;
        }

        throw new InvalidOperationException($"No post form carries '{marker}'.");
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, IEnumerable<KeyValuePair<string, string>> fields) =>
        client.PostAsync(path, new FormUrlEncodedContent(fields), Ct);

    [Fact]
    public async Task AdminPage_Anonymous_RedirectsToSignIn()
    {
        using HttpClient client = Bare(site.Factory);

        using HttpResponseMessage response = await client.GetAsync("/ru/admin", Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/signin?ReturnUrl=%2Fru%2Fadmin", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task AdminPage_NonAdmin_RedirectsToDenied()
    {
        using HttpClient client = await SignedInAsync(NextTelegramId(), "Plain Reader");

        using HttpResponseMessage response = await client.GetAsync("/ru/admin", Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/signin?denied=1&ReturnUrl=%2Fru%2Fadmin", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task AdminPage_NonAdminWithQuery_RevealsNothingAboutTheUser()
    {
        UserAccess target = await CreateUserAsync(NextTelegramId(), "Hidden Target");
        using HttpClient client = await SignedInAsync(NextTelegramId(), "Plain Reader");

        (HttpStatusCode status, string body, HttpResponseMessage response) = await GetAsync(client, $"/ru/admin?q={target.TelegramUserId}");
        using (response)
        {
            Assert.Equal(HttpStatusCode.Redirect, status);
            Assert.DoesNotContain("Hidden Target", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Hidden Target", response.Headers.Location?.OriginalString ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AdminPage_UnsupportedCulture_Is404()
    {
        using HttpClient client = await SignedInAsync(SignInSiteFixture.AdminTelegramId, "Site Admin");

        using HttpResponseMessage response = await client.GetAsync("/xx/admin", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AdminPage_Admin_ShowsHealthImportsAndUsers()
    {
        UserAccess member = await CreateUserAsync(NextTelegramId(), "Visible Member");
        using HttpClient client = await SignedInAsync(SignInSiteFixture.AdminTelegramId, "Site Admin");
        AdminOverview overview = await WithOverviewAsync();
        ImportRow active = overview.RecentImports.Single(r => r.IsActive);

        (HttpStatusCode status, string ru, HttpResponseMessage response) = await GetAsync(client, "/ru/admin");
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains("Администрирование", ru, StringComparison.Ordinal);
            Assert.Contains("Контент импортирован", ru, StringComparison.Ordinal);
            Assert.Contains("База данных доступна", ru, StringComparison.Ordinal);
            Assert.Contains(active.Hash[..12], ru, StringComparison.Ordinal);
            Assert.DoesNotContain(active.Hash, ru, StringComparison.Ordinal);
            Assert.Contains($"datetime=\"{active.ImportedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}\"", ru, StringComparison.Ordinal);
            Assert.Contains("Visible Member", ru, StringComparison.Ordinal);
            Assert.Contains(member.TelegramUserId.ToString(CultureInfo.InvariantCulture), ru, StringComparison.Ordinal);
            Assert.Contains("class=\"table-scroll\"", ru, StringComparison.Ordinal);
        }

        (_, string en, HttpResponseMessage enResponse) = await GetAsync(client, "/en/admin");
        using (enResponse)
        {
            Assert.Contains("Administration", en, StringComparison.Ordinal);
            Assert.Contains("Content imported", en, StringComparison.Ordinal);
            Assert.DoesNotContain("Администрирование", en, StringComparison.Ordinal);
            Assert.DoesNotContain("Контент импортирован", en, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AdminPage_Search_FiltersEncodesAndKeepsTheQuery()
    {
        long id = NextTelegramId();
        await WithUsersAsync(users => users.SignInAsync(new TelegramProfile(id, "xss_user", "<script>alert(1)</script>"), Ct));
        long other = NextTelegramId();
        await CreateUserAsync(other, "Unrelated Person");
        using HttpClient client = await SignedInAsync(SignInSiteFixture.AdminTelegramId, "Site Admin");

        (_, string body, HttpResponseMessage response) = await GetAsync(client, "/en/admin?q=xss_user");
        using (response)
        {
            Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", body, StringComparison.Ordinal);
            Assert.DoesNotContain("<script>alert(1)", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Unrelated Person", body, StringComparison.Ordinal);
            Assert.Contains("value=\"xss_user\"", body, StringComparison.Ordinal);
            Assert.Contains("method=\"get\"", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AdminPage_BlockUser_RotatesStampAndShowsBlocked()
    {
        UserAccess target = await CreateUserAsync(NextTelegramId(), "Soon Blocked");
        using HttpClient client = await SignedInAsync(SignInSiteFixture.AdminTelegramId, "Site Admin");
        string path = $"/ru/admin?q={target.TelegramUserId}";
        (_, string page, HttpResponseMessage first) = await GetAsync(client, path);
        first.Dispose();
        Dictionary<string, string> fields = FormFieldsFor(page, target.UserId.Value);
        Assert.Contains("__RequestVerificationToken", fields.Keys);

        using HttpResponseMessage response = await PostAsync(client, path, fields);
        string body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        UserAccess after = await WithUsersAsync(users => users.GetAccessAsync(target.TelegramUserId, Ct)) ?? throw new InvalidOperationException();
        Assert.True(after.IsBlocked);
        Assert.NotEqual(target.SecurityStamp, after.SecurityStamp);
        Assert.Contains("Заблокирован", body, StringComparison.Ordinal);
        Assert.Contains("Пользователь заблокирован", body, StringComparison.Ordinal);
        Assert.Contains("Разблокировать", body, StringComparison.Ordinal);

        // The same row now offers the opposite action, and posting it restores access.
        Dictionary<string, string> unblock = FormFieldsFor(body, target.UserId.Value);
        using HttpResponseMessage restored = await PostAsync(client, path, unblock);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        UserAccess final = await WithUsersAsync(users => users.GetAccessAsync(target.TelegramUserId, Ct)) ?? throw new InvalidOperationException();
        Assert.False(final.IsBlocked);
        Assert.NotEqual(after.SecurityStamp, final.SecurityStamp);
        Assert.Contains("Пользователь разблокирован", await restored.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminPage_BlockWithoutToken_Returns400()
    {
        UserAccess target = await CreateUserAsync(NextTelegramId(), "Token Check");
        using HttpClient client = await SignedInAsync(SignInSiteFixture.AdminTelegramId, "Site Admin");
        string path = $"/ru/admin?q={target.TelegramUserId}";
        (_, string page, HttpResponseMessage first) = await GetAsync(client, path);
        first.Dispose();
        Dictionary<string, string> fields = FormFieldsFor(page, target.UserId.Value);
        fields.Remove("__RequestVerificationToken");

        using HttpResponseMessage response = await PostAsync(client, path, fields);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        UserAccess after = await WithUsersAsync(users => users.GetAccessAsync(target.TelegramUserId, Ct)) ?? throw new InvalidOperationException();
        Assert.False(after.IsBlocked);
        Assert.Equal(target.SecurityStamp, after.SecurityStamp);
    }

    [Fact]
    public async Task AdminPage_BlockConfiguredAdmin_ShowsProtectedMessage()
    {
        UserAccess protectedAdmin = await CreateUserAsync(SecondAdminTelegramId, "Second Admin");
        using HttpClient client = await SignedInAsync(SignInSiteFixture.AdminTelegramId, "Site Admin");
        UserAccess other = await CreateUserAsync(NextTelegramId(), "Ordinary Member");
        string path = $"/ru/admin?q={other.TelegramUserId}";
        (_, string page, HttpResponseMessage first) = await GetAsync(client, path);
        first.Dispose();
        // The page offers no Block button for a configured admin, so the request is forged from another row's form.
        Dictionary<string, string> fields = FormFieldsFor(page, other.UserId.Value);
        fields["userId"] = protectedAdmin.UserId.Value;
        fields["blocked"] = "true";

        using HttpResponseMessage response = await PostAsync(client, path, fields);
        string body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Администратора из конфигурации нельзя заблокировать", body, StringComparison.Ordinal);
        UserAccess after = await WithUsersAsync(users => users.GetAccessAsync(SecondAdminTelegramId, Ct)) ?? throw new InvalidOperationException();
        Assert.False(after.IsBlocked);
        Assert.Equal(protectedAdmin.SecurityStamp, after.SecurityStamp);
    }

    [Fact]
    public async Task AdminPage_ConfiguredAdminRow_ShowsLabelInsteadOfBlockButton()
    {
        UserAccess configured = await CreateUserAsync(SecondAdminTelegramId, "Second Admin");
        using HttpClient client = await SignedInAsync(SignInSiteFixture.AdminTelegramId, "Site Admin");

        (_, string en, HttpResponseMessage response) = await GetAsync(client, $"/en/admin?q={SecondAdminTelegramId}");
        using (response)
        {
            Assert.Contains("Configured admin", en, StringComparison.Ordinal);
            Assert.Throws<InvalidOperationException>(() => FormFieldsFor(en, configured.UserId.Value));
            Assert.DoesNotContain(">Block<", en, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AdminPage_WhenTheDatabaseIsUnreachable_ShowsTheStateInsteadOfFailing()
    {
        await using FakeTelegramOidcServer telegram = await FakeTelegramOidcServer.StartAsync(TrainerWebFactoryOptions.TelegramClientId);
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions
        {
            Telegram = telegram,
            Settings = new Dictionary<string, string?> { ["App:AdminTelegramIds"] = "777000" },
            ConfigureServices = services => services.Replace(ServiceDescriptor.Scoped<IAdminQueries, DownDatabaseQueries>()),
        });
        await factory.WaitUntilReadyAsync();
        using HttpClient client = Browser(factory);
        using HttpResponseMessage callback = await TelegramSignInDriver.SignInAsync(
            client, telegram, new FakeTelegramIdentity(SignInSiteFixture.AdminTelegramId, "Site Admin", null), "/ru", Ct);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);

        using HttpResponseMessage response = await client.GetAsync("/en/admin", Ct);
        string body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Database unreachable", body, StringComparison.Ordinal);
        Assert.Contains("No users found.", body, StringComparison.Ordinal);
    }

    /// <summary>Reports an unreachable database and fails the way a dropped connection would if the user list is still asked for.</summary>
    private sealed class DownDatabaseQueries : IAdminQueries
    {
        public Task<AdminOverview> GetOverviewAsync(CancellationToken ct) =>
            Task.FromResult(new AdminOverview(false, [], 0, 0, 0));

        public Task<UserPage> GetUsersAsync(string? search, int page, CancellationToken ct) =>
            throw new Npgsql.NpgsqlException("The database is down.");
    }

    [Theory]
    [InlineData("01KBZ3Q8N9V2K3M4P5R6S7T8WA", "true")]
    [InlineData("not-a-ulid", "true")]
    [InlineData("REAL", "maybe")]
    public async Task AdminPage_BlockWithUnknownTargetOrBadValue_ShowsNotFoundAndChangesNothing(string userId, string blocked)
    {
        UserAccess target = await CreateUserAsync(NextTelegramId(), "Stays Active");
        using HttpClient client = await SignedInAsync(SignInSiteFixture.AdminTelegramId, "Site Admin");
        string path = $"/en/admin?q={target.TelegramUserId}";
        (_, string page, HttpResponseMessage first) = await GetAsync(client, path);
        first.Dispose();
        Dictionary<string, string> fields = FormFieldsFor(page, target.UserId.Value);
        fields["userId"] = userId == "REAL" ? target.UserId.Value : userId;
        fields["blocked"] = blocked;

        using HttpResponseMessage response = await PostAsync(client, path, fields);
        string body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("User not found.", body, StringComparison.Ordinal);
        UserAccess after = await WithUsersAsync(users => users.GetAccessAsync(target.TelegramUserId, Ct)) ?? throw new InvalidOperationException();
        Assert.False(after.IsBlocked);
    }

    [Fact]
    public async Task AdminPage_PostFromNonAdmin_IsDeniedAndChangesNothing()
    {
        UserAccess target = await CreateUserAsync(NextTelegramId(), "Safe Target");
        using HttpClient admin = await SignedInAsync(SignInSiteFixture.AdminTelegramId, "Site Admin");
        string path = $"/ru/admin?q={target.TelegramUserId}";
        (_, string page, HttpResponseMessage first) = await GetAsync(admin, path);
        first.Dispose();
        Dictionary<string, string> fields = FormFieldsFor(page, target.UserId.Value);

        using HttpClient reader = await SignedInAsync(NextTelegramId(), "Plain Reader");
        (_, string readerPage, HttpResponseMessage readerGet) = await GetAsync(reader, "/ru");
        readerGet.Dispose();
        fields["__RequestVerificationToken"] = AntiforgeryToken(readerPage);
        using HttpResponseMessage response = await PostAsync(reader, path, fields);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/signin?denied=1&ReturnUrl={Uri.EscapeDataString(path)}", response.Headers.Location?.OriginalString);
        UserAccess after = await WithUsersAsync(users => users.GetAccessAsync(target.TelegramUserId, Ct)) ?? throw new InvalidOperationException();
        Assert.False(after.IsBlocked);
        Assert.Equal(target.SecurityStamp, after.SecurityStamp);
    }

    [Fact]
    public async Task AdminPage_IsNoindexPrivateAndNotCompressed()
    {
        using HttpClient client = await SignedInAsync(SignInSiteFixture.AdminTelegramId, "Site Admin");
        using HttpRequestMessage request = new(HttpMethod.Get, "/en/admin");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "br, gzip");

        using HttpResponseMessage response = await client.SendAsync(request, Ct);
        string body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<meta name=\"robots\" content=\"noindex\"", body, StringComparison.Ordinal);
        Assert.Empty(response.Content.Headers.ContentEncoding);
        string cacheControl = string.Join(", ", response.Headers.GetValues("Cache-Control"));
        Assert.Contains("private", cacheControl, StringComparison.Ordinal);
        Assert.Contains("no-store", cacheControl, StringComparison.Ordinal);
        Assert.False(response.Headers.Contains("Age"));
        Assert.Contains("name=\"__RequestVerificationToken\"", body, StringComparison.Ordinal);
    }

    /// <summary>The hidden fields of the single resolve form plus the id carried by the clicked button, as a browser submits them.</summary>
    private static Dictionary<string, string> ResolveFields(string html, string reportId)
    {
        Dictionary<string, string> fields = FormFieldsFor(html, "value=\"resolve-report\"");
        Assert.Contains($"name=\"reportId\" value=\"{reportId}\"", html, StringComparison.Ordinal);
        fields["reportId"] = reportId;
        return fields;
    }

    private const string ClosureQuestionId = "01KBZ3Q8N9V2K3M4P5R6S7T8WA";

    private async Task<string> SeedReportAsync(UserAccess reporter, string culture, ReportReason reason, string? comment)
    {
        await using (AsyncServiceScope scope = site.Factory.Services.CreateAsyncScope())
        {
            ReportResult result = await scope.ServiceProvider.GetRequiredService<IQuestionFeedbackService>()
                .ReportAsync(reporter.UserId, new QuestionId(ClosureQuestionId), new CultureKey(culture), reason, comment, Ct);
            Assert.Equal(ReportOutcome.Created, result.Outcome);
        }

        await using TrainerDbContext db = PostgresFixture.CreateContext(site.Factory.ConnectionString);
        string userId = reporter.UserId.Value;
        return await db.Database
            .SqlQuery<string>($"SELECT id AS \"Value\" FROM question_reports WHERE user_id = {userId}")
            .SingleAsync(Ct);
    }

    private async Task<bool> IsResolvedAsync(string reportId)
    {
        await using TrainerDbContext db = PostgresFixture.CreateContext(site.Factory.ConnectionString);
        return await db.Database
            .SqlQuery<bool>($"SELECT resolved_at IS NOT NULL AS \"Value\" FROM question_reports WHERE id = {reportId}")
            .SingleAsync(Ct);
    }

    private static string SectionOf(string html, string id)
    {
        string tail = html[html.IndexOf($"id=\"{id}\"", StringComparison.Ordinal)..];
        return tail[..tail.IndexOf("</section>", StringComparison.Ordinal)];
    }

    [Fact]
    public async Task Admin_ListsOpenReportsEncodesCommentAndResolves()
    {
        UserAccess reporter = await CreateUserAsync(NextTelegramId(), "Careful Reporter");
        string reportId = await SeedReportAsync(reporter, "ru", ReportReason.WrongAnswer, "<script>x</script>\nsecond line");
        using HttpClient client = await SignedInAsync(SignInSiteFixture.AdminTelegramId, "Site Admin");

        (HttpStatusCode status, string body, HttpResponseMessage response) = await GetAsync(client, "/en/admin");
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains("&lt;script&gt;x&lt;/script&gt;", body, StringComparison.Ordinal);
            Assert.DoesNotContain("<script>x</script>", body, StringComparison.Ordinal);
            Assert.Contains("<span class=\"admin-report__comment\">", body, StringComparison.Ordinal);
            // The question opens in the culture the reporter read it in, whatever culture the admin page is in.
            Assert.Contains("href=\"/ru/dotnet/questions/closure-for-loop\"", body, StringComparison.Ordinal);
            Assert.Contains("Wrong answer", body, StringComparison.Ordinal);
            Assert.Contains("Careful Reporter", body, StringComparison.Ordinal);
            Assert.Contains(reporter.TelegramUserId.ToString(CultureInfo.InvariantCulture), body, StringComparison.Ordinal);
        }

        Dictionary<string, string> fields = ResolveFields(body, reportId);
        Assert.Contains("__RequestVerificationToken", fields.Keys);
        using HttpResponseMessage resolved = await PostAsync(client, "/en/admin", fields);

        // Post-redirect-get: a refresh repeats a GET, not the resolve.
        Assert.Equal(HttpStatusCode.Found, resolved.StatusCode);
        Assert.Contains("/en/admin?notice=report-resolved", resolved.Headers.Location?.OriginalString, StringComparison.Ordinal);
        Assert.True(await IsResolvedAsync(reportId));
        (_, string afterBody, HttpResponseMessage afterResponse) = await GetAsync(client, resolved.Headers.Location!.PathAndQuery);
        using (afterResponse)
        {
            Assert.Contains("Report resolved.", afterBody, StringComparison.Ordinal);
            Assert.DoesNotContain("<script>x</script>", afterBody, StringComparison.Ordinal);
            Assert.DoesNotContain($"value=\"{reportId}\"", afterBody, StringComparison.Ordinal);
        }

        // The resolved list keeps it as history, without a resolve button.
        (_, string history, HttpResponseMessage historyResponse) = await GetAsync(client, "/en/admin?reports=resolved");
        using (historyResponse)
        {
            Assert.Contains("&lt;script&gt;x&lt;/script&gt;", history, StringComparison.Ordinal);
            Assert.Contains("aria-current=\"true\"", history, StringComparison.Ordinal);
            Assert.DoesNotContain($"value=\"{reportId}\"", history, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Admin_DoubleSubmitOfTheSameResolveForm_RedirectsWithAnAlreadyResolvedNotice()
    {
        UserAccess first = await CreateUserAsync(NextTelegramId(), "Double Submitter");
        UserAccess second = await CreateUserAsync(NextTelegramId(), "Bystander");
        string reportId = await SeedReportAsync(first, "en", ReportReason.Other, "closed once");
        string otherId = await SeedReportAsync(second, "en", ReportReason.Other, "stays open");
        using HttpClient client = await SignedInAsync(SignInSiteFixture.AdminTelegramId, "Site Admin");
        (_, string page, HttpResponseMessage response) = await GetAsync(client, "/en/admin");
        response.Dispose();
        Dictionary<string, string> fields = ResolveFields(page, reportId);

        using HttpResponseMessage firstPost = await PostAsync(client, "/en/admin", fields);
        using HttpResponseMessage secondPost = await PostAsync(client, "/en/admin", fields);

        Assert.Equal(HttpStatusCode.Found, firstPost.StatusCode);
        Assert.Contains("/en/admin?notice=report-resolved", firstPost.Headers.Location?.OriginalString, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Found, secondPost.StatusCode);
        Assert.Contains("/en/admin?notice=report-not-found", secondPost.Headers.Location?.OriginalString, StringComparison.Ordinal);
        (_, string body, HttpResponseMessage followed) = await GetAsync(client, secondPost.Headers.Location!.PathAndQuery);
        using (followed)
        {
            Assert.Contains("Report not found or already resolved.", body, StringComparison.Ordinal);
        }

        Assert.True(await IsResolvedAsync(reportId));
        Assert.False(await IsResolvedAsync(otherId));
    }

    [Fact]
    public async Task Admin_ReportsPaging_KeepsStateAndClampsThePage()
    {
        // 25 per page: a 26th open report makes a second page, whatever other tests left open.
        for (int i = 0; i < 26; i++)
        {
            await SeedReportAsync(await CreateUserAsync(NextTelegramId(), "Pager Reporter " + i), "en", ReportReason.Other, "paging " + i);
        }

        using HttpClient client = await SignedInAsync(SignInSiteFixture.AdminTelegramId, "Site Admin");

        (_, string first, HttpResponseMessage firstResponse) = await GetAsync(client, "/en/admin?q=nobody-matches-this");
        using (firstResponse)
        {
            Assert.Contains("rel=\"next\" href=\"/en/admin?q=nobody-matches-this&amp;reportsPage=2\"", first, StringComparison.Ordinal);
            Assert.DoesNotContain("rel=\"prev\"", first, StringComparison.Ordinal);
            Assert.Equal(25, Regex.Count(first, "form=\"resolve-report\" name=\"reportId\"", RegexOptions.CultureInvariant));
        }

        (HttpStatusCode secondStatus, string second, HttpResponseMessage secondResponse) = await GetAsync(client, "/en/admin?reportsPage=2");
        using (secondResponse)
        {
            Assert.Equal(HttpStatusCode.OK, secondStatus);
            Assert.Contains("rel=\"prev\" href=\"/en/admin\"", second, StringComparison.Ordinal);
            Assert.DoesNotContain("rel=\"next\"", second, StringComparison.Ordinal);
        }

        // Out of range is clamped to the last page instead of showing an empty list.
        (HttpStatusCode farStatus, string far, HttpResponseMessage farResponse) = await GetAsync(client, "/en/admin?reportsPage=999");
        using (farResponse)
        {
            Assert.Equal(HttpStatusCode.OK, farStatus);
            Assert.Contains("rel=\"prev\"", far, StringComparison.Ordinal);
            Assert.DoesNotContain("rel=\"next\"", far, StringComparison.Ordinal);
        }

        // The filter survives the pager links.
        (_, string resolved, HttpResponseMessage resolvedResponse) = await GetAsync(client, "/en/admin?reports=resolved");
        using (resolvedResponse)
        {
            Assert.DoesNotContain("reportsPage=2", resolved, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Admin_ResolveButtons_OfTwoReportsOnOneQuestion_HaveDifferentAccessibleNames()
    {
        await SeedReportAsync(await CreateUserAsync(NextTelegramId(), "Namer Alpha"), "en", ReportReason.Other, "a");
        await SeedReportAsync(await CreateUserAsync(NextTelegramId(), "Namer Beta"), "en", ReportReason.Other, "b");
        using HttpClient client = await SignedInAsync(SignInSiteFixture.AdminTelegramId, "Site Admin");

        (_, string body, HttpResponseMessage response) = await GetAsync(client, "/en/admin");
        using (response)
        {
            string[] names = [.. Regex.Matches(body, "form=\"resolve-report\"[^>]*>\\s*Resolve<span class=\"visually-hidden\">(?<n>[^<]*)</span>", RegexOptions.CultureInvariant)
                .Select(m => WebUtility.HtmlDecode(m.Groups["n"].Value).Trim())];
            Assert.Contains(names, n => n.Contains("reported by Namer Alpha", StringComparison.Ordinal));
            Assert.Contains(names, n => n.Contains("reported by Namer Beta", StringComparison.Ordinal));
            Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public async Task Admin_ReportsAndMostDisliked_AreLocalised()
    {
        UserAccess reporter = await CreateUserAsync(NextTelegramId(), "Russian Reader");
        await SeedReportAsync(reporter, "en", ReportReason.BrokenLink, null);
        await using (AsyncServiceScope scope = site.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IQuestionFeedbackService>()
                .SetRatingAsync(reporter.UserId, new QuestionId(ClosureQuestionId), QuestionRating.Dislike, Ct);
        }

        using HttpClient client = await SignedInAsync(SignInSiteFixture.AdminTelegramId, "Site Admin");

        (_, string ru, HttpResponseMessage ruResponse) = await GetAsync(client, "/ru/admin");
        using (ruResponse)
        {
            Assert.Contains("Жалобы", ru, StringComparison.Ordinal);
            Assert.Contains("Не работает ссылка", ru, StringComparison.Ordinal);
            Assert.Contains("Без комментария", ru, StringComparison.Ordinal);
            Assert.Contains("Вопросы с наибольшим числом дизлайков", ru, StringComparison.Ordinal);
            Assert.Contains("href=\"/ru/dotnet/questions/closure-for-loop\"", SectionOf(ru, "admin-disliked"), StringComparison.Ordinal);
            Assert.Contains("Что выведет код с лямбдами", SectionOf(ru, "admin-disliked"), StringComparison.Ordinal);
            Assert.DoesNotContain("What does the code print", SectionOf(ru, "admin-disliked"), StringComparison.Ordinal);
        }

        (_, string en, HttpResponseMessage enResponse) = await GetAsync(client, "/en/admin");
        using (enResponse)
        {
            Assert.Contains(">Reports<", en, StringComparison.Ordinal);
            Assert.Contains("Broken link", en, StringComparison.Ordinal);
            Assert.Contains("Most disliked questions", en, StringComparison.Ordinal);
            Assert.DoesNotContain("Жалобы", en, StringComparison.Ordinal);
            Assert.Contains("href=\"/en/dotnet/questions/closure-for-loop\"", SectionOf(en, "admin-disliked"), StringComparison.Ordinal);
            Assert.Contains("What does the code print when lambdas capture", SectionOf(en, "admin-disliked"), StringComparison.Ordinal);
            Assert.DoesNotContain("Что выведет код", SectionOf(en, "admin-disliked"), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Admin_NonAdminCannotResolve()
    {
        UserAccess reporter = await CreateUserAsync(NextTelegramId(), "Pending Reporter");
        string reportId = await SeedReportAsync(reporter, "en", ReportReason.Outdated, "still open");
        using HttpClient admin = await SignedInAsync(SignInSiteFixture.AdminTelegramId, "Site Admin");
        (_, string page, HttpResponseMessage first) = await GetAsync(admin, "/en/admin");
        first.Dispose();
        Dictionary<string, string> fields = ResolveFields(page, reportId);

        using HttpClient reader = await SignedInAsync(NextTelegramId(), "Plain Reader");
        (_, string readerPage, HttpResponseMessage readerGet) = await GetAsync(reader, "/en");
        readerGet.Dispose();
        fields["__RequestVerificationToken"] = AntiforgeryToken(readerPage);
        using HttpResponseMessage response = await PostAsync(reader, "/en/admin", fields);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/signin?denied=1&ReturnUrl=%2Fen%2Fadmin", response.Headers.Location?.OriginalString);
        Assert.False(await IsResolvedAsync(reportId));
    }

    private async Task<AdminOverview> WithOverviewAsync()
    {
        await using AsyncServiceScope scope = site.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAdminQueries>().GetOverviewAsync(Ct);
    }
}
