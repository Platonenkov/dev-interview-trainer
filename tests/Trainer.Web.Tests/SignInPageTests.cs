using System.Net;
using Trainer.TestSupport;
using Trainer.Web.Caching;
using static Trainer.Web.Tests.SignInTestHelpers;

namespace Trainer.Web.Tests;

/// <summary>The sign-in page and the header account controls, on a site wired to a fake Telegram provider.</summary>
public sealed class SignInPageTests(SignInSiteFixture site) : IClassFixture<SignInSiteFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(HttpResponseMessage Response, string Body)> GetAsync(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.GetAsync(path, Ct);
        return (response, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<HttpClient> SignedInBrowserAsync(FakeTelegramIdentity who)
    {
        HttpClient client = Browser(site.Factory);
        using HttpResponseMessage callback = await TelegramSignInDriver.SignInAsync(client, site.Telegram, who, "/en", Ct);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.NotNull(SetCookie(callback, AuthCookieNames.Session));
        return client;
    }

    [Fact]
    public async Task SignInPage_Anonymous_HasChallengeLinkNoFormNoCookie()
    {
        using HttpClient client = Bare(site.Factory);

        (HttpResponseMessage response, string body) = await GetAsync(client, "/en/signin?returnUrl=%2Fen%2Fdotnet%2Fquestions");
        using HttpResponseMessage disposable = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "href=\"/signin-telegram/challenge?returnUrl=%2Fen%2Fdotnet%2Fquestions\" data-enhance-nav=\"false\"",
            body,
            StringComparison.Ordinal);
        Assert.Contains("Sign in with Telegram", body, StringComparison.Ordinal);
        Assert.Contains("<meta name=\"robots\" content=\"noindex\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<form", body, StringComparison.OrdinalIgnoreCase);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData("/en/signin?returnUrl=%2Fen%2Fabout")]
    [InlineData("/en/signin/?returnUrl=%2Fen%2Fabout")]
    public async Task SignInPage_Anonymous_HeaderHasNoNestedSignInLink(string path)
    {
        using HttpClient client = Bare(site.Factory);

        (HttpResponseMessage response, string body) = await GetAsync(client, path);
        using HttpResponseMessage disposable = response;

        Assert.DoesNotContain("href=\"/en/signin?", body, StringComparison.Ordinal);
        Assert.DoesNotContain(">Sign in</a>", body, StringComparison.Ordinal);
        Assert.Contains("Sign in with Telegram", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignInPage_SignedIn_HeaderStillOffersSignOut()
    {
        using HttpClient client = await SignedInBrowserAsync(new FakeTelegramIdentity(520006, "Header Keeper", "header_keeper"));

        (HttpResponseMessage response, string body) = await GetAsync(client, "/en/signin");
        using HttpResponseMessage disposable = response;

        Assert.Contains(">Header Keeper</a>", body, StringComparison.Ordinal);
        Assert.Contains(">Sign out</button>", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://evil.test/x")]
    [InlineData("//evil.test")]
    [InlineData("/\\evil.test")]
    [InlineData("%2F%2Fevil.test")]
    [InlineData("/en/x")]
    [InlineData("/ru%0d%0aX")]
    public async Task SignInPage_UnsafeReturnPaths_BecomeCultureHome(string rawReturnUrl)
    {
        using HttpClient client = Bare(site.Factory);

        (HttpResponseMessage response, string body) = await GetAsync(client, "/ru/signin?returnUrl=" + rawReturnUrl);
        using HttpResponseMessage disposable = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("href=\"/signin-telegram/challenge?returnUrl=%2Fru\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SignInPage_ShowsDeniedAndErrorAlerts()
    {
        using HttpClient client = Bare(site.Factory);

        (HttpResponseMessage denied, string deniedBody) = await GetAsync(client, "/en/signin?denied=1");
        (HttpResponseMessage error, string errorBody) = await GetAsync(client, "/ru/signin?error=1");
        denied.Dispose();
        error.Dispose();

        Assert.Contains("role=\"alert\">You cannot open that page, or your account is blocked", deniedBody, StringComparison.Ordinal);
        Assert.Contains("role=\"alert\">Вход не завершился, попробуйте ещё раз", errorBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignInPage_LanguageSwitchKeepsReturnUrl()
    {
        using HttpClient client = Bare(site.Factory);

        (HttpResponseMessage response, string body) = await GetAsync(client, "/ru/signin?returnUrl=%2Fru%2Fdotnet%2Fquestions");
        using HttpResponseMessage disposable = response;

        string target = "/en/signin?returnUrl=" + Uri.EscapeDataString("/en/dotnet/questions");
        Assert.Contains($"href=\"/lang/en?return={Uri.EscapeDataString(target)}\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Layout_Anonymous_ShowsSignInLinkWithCanonicalReturnUrl()
    {
        using HttpClient client = Bare(site.Factory);

        (HttpResponseMessage response, string body) = await GetAsync(client, "/ru/dotnet/questions?level=MIDDLE&utm_source=x");
        using HttpResponseMessage disposable = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("href=\"/ru/signin?returnUrl=%2Fru%2Fdotnet%2Fquestions%3Flevel%3Dmiddle\"", body, StringComparison.Ordinal);
        Assert.Contains(">Войти</a>", body, StringComparison.Ordinal);
        Assert.DoesNotContain("utm_source", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<form method=\"post\"", body, StringComparison.Ordinal);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Layout_SignedIn_ShowsNameAndLogoutFormWithToken()
    {
        using HttpClient client = await SignedInBrowserAsync(new FakeTelegramIdentity(520001, "Layout Person", "layout_person"));

        (HttpResponseMessage response, string body) = await GetAsync(client, "/en");
        using HttpResponseMessage disposable = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(">Layout Person</a>", body, StringComparison.Ordinal);
        Assert.Contains("href=\"/en/dotnet/profile\"", body, StringComparison.Ordinal);
        Assert.Contains("<form class=\"account__logout\" method=\"post\" action=\"/signin-telegram/logout\">", body, StringComparison.Ordinal);
        Assert.NotEmpty(AntiforgeryToken(body));
        Assert.Contains(">Sign out</button>", body, StringComparison.Ordinal);
        Assert.DoesNotContain(">Sign in</a>", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Layout_SignedInWithoutNames_ShowsTelegramUser()
    {
        using HttpClient client = await SignedInBrowserAsync(new FakeTelegramIdentity(520002, null, null));

        (HttpResponseMessage response, string body) = await GetAsync(client, "/ru");
        using HttpResponseMessage disposable = response;

        Assert.Contains(">Пользователь Telegram</a>", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignInPage_SignedIn_ShowsNameAndLinkBack()
    {
        using HttpClient client = await SignedInBrowserAsync(new FakeTelegramIdentity(520003, null, "only_username"));

        (HttpResponseMessage response, string body) = await GetAsync(client, "/en/signin?returnUrl=%2Fen%2Fabout");
        using HttpResponseMessage disposable = response;

        Assert.Contains("Signed in as only_username.", body, StringComparison.Ordinal);
        Assert.Contains("href=\"/en/about\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("/signin-telegram/challenge", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignedInPublicPage_AndSignInPage_PublishOwner()
    {
        const string Script = "<script src=\"/js/account-publish(\\.[a-z0-9]+)?\\.js\" data-owner=\"{0}\" defer></script>";
        using HttpClient anonymous = Bare(site.Factory);
        using HttpClient signedIn = await SignedInBrowserAsync(new FakeTelegramIdentity(520007, "Publisher", "publisher"));

        (HttpResponseMessage publicAnon, string publicAnonBody) = await GetAsync(anonymous, "/en/dotnet/questions");
        (HttpResponseMessage signInAnon, string signInAnonBody) = await GetAsync(anonymous, "/en/signin");
        (HttpResponseMessage publicUser, string publicUserBody) = await GetAsync(signedIn, "/en/dotnet/questions");
        (HttpResponseMessage signInUser, string signInUserBody) = await GetAsync(signedIn, "/ru/signin");
        publicAnon.Dispose();
        signInAnon.Dispose();
        publicUser.Dispose();
        signInUser.Dispose();

        // Anonymous public pages are cached and shared: they publish nothing. Signing out lands on the sign-in page.
        Assert.DoesNotContain("account-publish", publicAnonBody, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(signInAnonBody, string.Format(System.Globalization.CultureInfo.InvariantCulture, Script, "anon")));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(publicUserBody, string.Format(System.Globalization.CultureInfo.InvariantCulture, Script, "520007")));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(signInUserBody, "account-publish"));
        Assert.Matches(string.Format(System.Globalization.CultureInfo.InvariantCulture, Script, "520007"), signInUserBody);
    }

    [Fact]
    public async Task Logout_WithToken_ClearsSessionAndLandsOnSignIn()
    {
        using HttpClient client = await SignedInBrowserAsync(new FakeTelegramIdentity(520004, "Leaving", "leaving"));
        (HttpResponseMessage page, string body) = await GetAsync(client, "/en");
        page.Dispose();
        using FormUrlEncodedContent form = new([new KeyValuePair<string, string>("__RequestVerificationToken", AntiforgeryToken(body))]);

        using HttpResponseMessage logout = await client.PostAsync("/signin-telegram/logout", form, Ct);

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/signin", logout.Headers.Location?.OriginalString);
        string cleared = SetCookie(logout, AuthCookieNames.Session) ?? throw new InvalidOperationException("session cookie not cleared");
        Assert.Contains("expires=Thu, 01 Jan 1970", cleared, StringComparison.OrdinalIgnoreCase);
        (HttpResponseMessage after, string afterBody) = await GetAsync(client, "/en");
        after.Dispose();
        Assert.Contains(">Sign in</a>", afterBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Leaving", afterBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logout_WithoutToken_Returns400()
    {
        using HttpClient client = await SignedInBrowserAsync(new FakeTelegramIdentity(520005, "Staying", "staying"));
        using FormUrlEncodedContent form = new([]);

        using HttpResponseMessage logout = await client.PostAsync("/signin-telegram/logout", form, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, logout.StatusCode);
        Assert.Null(SetCookie(logout, AuthCookieNames.Session));
    }
}
