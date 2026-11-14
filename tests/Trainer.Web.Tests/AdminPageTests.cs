using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Trainer.Application.Admin;
using Trainer.Application.Users;
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

    private async Task<AdminOverview> WithOverviewAsync()
    {
        await using AsyncServiceScope scope = site.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAdminQueries>().GetOverviewAsync(Ct);
    }
}
