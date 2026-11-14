using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Trainer.Application.Stacks;
using Trainer.Application.Users;
using Trainer.Contracts;
using Trainer.Domain.Keys;
using Trainer.Infrastructure.Persistence;
using Trainer.TestSupport;
using Trainer.Web.Auth;
using Trainer.Web.Islands;
using Trainer.Web.Startup;
using static Trainer.Web.Tests.SignInTestHelpers;

namespace Trainer.Web.Tests;

/// <summary>
/// Where the WebAssembly runtime may appear: only on island pages, which are noindex, never output-cached and the only
/// responses whose CSP allows WebAssembly compilation. Public pages stay free of Blazor's script (spec 8.2).
/// </summary>
public sealed partial class IslandHostingTests(SignInSiteFixture site, PostgresFixture postgres) : IClassFixture<SignInSiteFixture>
{
    private const string PracticePath = "/en/dotnet/practice";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("<!--Blazor-WebAssembly-Component-State:(?<state>[A-Za-z0-9+/=]+)-->", RegexOptions.CultureInvariant)]
    private static partial Regex WebAssemblyState();

    private static async Task<(HttpResponseMessage Response, string Body)> GetAsync(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.GetAsync(path, Ct);
        return (response, await response.Content.ReadAsStringAsync(Ct));
    }

    private static string Csp(HttpResponseMessage response) => Assert.Single(response.Headers.GetValues("Content-Security-Policy"));

    [GeneratedRegex("<script type=\"importmap\" nonce=\"(?<nonce>[A-Za-z0-9_-]+)\">(?<map>[^<]*)</script>", RegexOptions.CultureInvariant)]
    private static partial Regex ImportMap();

    private static string ImportMapNonce(string body)
    {
        Match match = ImportMap().Match(body);
        Assert.True(match.Success, "The island page has no import map carrying a nonce.");
        return match.Groups["nonce"].Value;
    }

    [Theory]
    [InlineData("/ru")]
    [InlineData("/ru/dotnet/questions")]
    [InlineData("/ru/dotnet/questions/closure-for-loop")]
    [InlineData("/ru/signin")]
    public async Task PublicPages_HaveNoBlazorScript(string path)
    {
        using HttpClient client = Bare(site.Factory);

        (HttpResponseMessage response, string body) = await GetAsync(client, path);
        using HttpResponseMessage disposable = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        RuntimeAssertions.AssertNoRuntime(body);
        Assert.DoesNotContain("<link rel=\"preload\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("islands-start", body, StringComparison.Ordinal);
        Assert.DoesNotContain("js/shortcuts", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<base ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("importmap", body, StringComparison.Ordinal);
        Assert.Contains("<a class=\"skip-link\" href=\"#main\">", body, StringComparison.Ordinal);
        Assert.DoesNotContain("wasm-unsafe-eval", Csp(response), StringComparison.Ordinal);
        Assert.Equal(SecurityHeadersMiddleware.ContentSecurityPolicy, Csp(response));
        // The render-mode convention (IslandEndpointConventions) is what keeps persisted component state, and with it an
        // antiforgery token and cookie, off these pages.
        Assert.DoesNotContain("Blazor-WebAssembly-Component-State", body, StringComparison.Ordinal);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task PublicPage_SignedIn_CarriesNoComponentStateAndNoClaimSet()
    {
        using HttpClient client = Browser(site.Factory);
        using HttpResponseMessage callback = await TelegramSignInDriver.SignInAsync(
            client, site.Telegram, new FakeTelegramIdentity(561_203L, "Public Reader", "public_reader"), "/en", Ct);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);

        (HttpResponseMessage response, string body) = await GetAsync(client, "/en/dotnet/questions");
        using HttpResponseMessage disposable = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("Blazor-WebAssembly-Component-State", body, StringComparison.Ordinal);
        Assert.DoesNotContain(TrainerClaimTypes.TelegramUserId, body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/en/dotnet/practice", "This part of the page could not start.")]
    [InlineData("/ru/dotnet/practice", "Эта часть страницы не смогла запуститься.")]
    public async Task IslandPage_CarriesHiddenBootFailureMessage(string path, string text)
    {
        using HttpClient client = Bare(site.Factory);

        (HttpResponseMessage response, string body) = await GetAsync(client, path);
        using HttpResponseMessage disposable = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Matches(@"<p class=""island-boot-error"" role=""alert"" hidden>\s*" + Regex.Escape(text), body);
        string script = await client.GetStringAsync(IslandsStartUrl(body), Ct);
        Assert.Contains("showBootFailure", script, StringComparison.Ordinal);
    }

    private static string IslandsStartUrl(string body) =>
        Regex.Match(body, @"<script src=""(?<url>/js/islands-start(\.[a-z0-9]+)?\.js)""></script>").Groups["url"].Value;

    [Fact]
    public async Task IslandPage_HasBlazorScriptAndWasmCsp()
    {
        using HttpClient client = Bare(site.Factory);

        (HttpResponseMessage response, string body) = await GetAsync(client, PracticePath);
        using HttpResponseMessage disposable = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Matches("<script src=\"/_framework/blazor\\.web(\\.[a-z0-9]+)?\\.js\" autostart=\"false\"></script>", body);
        Assert.Matches("<script src=\"/js/islands-start(\\.[a-z0-9]+)?\\.js\"></script>", body);
        Assert.Matches("<script src=\"/js/shortcuts(\\.[a-z0-9]+)?\\.js\"></script>", body);
        Assert.Matches("<link rel=\"stylesheet\" href=\"/css/islands(\\.[a-z0-9]+)?\\.css\" />", body);
        Assert.Contains(
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1, interactive-widget=resizes-content\">",
            body,
            StringComparison.Ordinal);
        Assert.Contains("<base href=\"/\">", body, StringComparison.Ordinal);
        Assert.Contains("<a class=\"skip-link\" href=\"/en/dotnet/practice#main\">", body, StringComparison.Ordinal);
        Assert.Contains("<div class=\"island\" data-island=\"practice\">", body, StringComparison.Ordinal);
        Assert.Contains("class=\"island-skeleton\"", body, StringComparison.Ordinal);
        Assert.Contains("<!--Blazor:{", body, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"webassembly\"", body, StringComparison.Ordinal);

        string csp = Csp(response);
        string nonce = ImportMapNonce(body);
        Assert.Equal(SecurityHeadersMiddleware.IslandContentSecurityPolicy(nonce), csp);
        Assert.StartsWith($"default-src 'self'; script-src 'self' 'wasm-unsafe-eval' 'nonce-{nonce}' {ThemeScript.CspSource}; ", csp, StringComparison.Ordinal);
        Assert.Contains("; base-uri 'self'; ", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("'unsafe-eval'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("'unsafe-inline'", csp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IslandPage_ImportMap_HasFreshNonceAndMapsTheRuntimeToItsFingerprint()
    {
        using HttpClient client = Bare(site.Factory);

        (HttpResponseMessage first, string firstBody) = await GetAsync(client, PracticePath);
        using HttpResponseMessage firstDisposable = first;
        (HttpResponseMessage second, string secondBody) = await GetAsync(client, PracticePath);
        using HttpResponseMessage secondDisposable = second;

        string nonce = ImportMapNonce(firstBody);
        Assert.Equal(24, nonce.Length);
        Assert.NotEqual(nonce, ImportMapNonce(secondBody));
        Assert.Contains($"'nonce-{nonce}'", Csp(first), StringComparison.Ordinal);
        Assert.DoesNotContain($"'nonce-{nonce}'", Csp(second), StringComparison.Ordinal);

        // Before blazor.web.js, which imports dotnet.js by its plain name; the preload names the fingerprinted file.
        Assert.True(
            firstBody.IndexOf("<script type=\"importmap\"", StringComparison.Ordinal) < firstBody.IndexOf("_framework/blazor.web", StringComparison.Ordinal),
            "The import map must precede the runtime script.");
        Match map = ImportMap().Match(firstBody);
        using JsonDocument imports = JsonDocument.Parse(WebUtility.HtmlDecode(map.Groups["map"].Value));
        string dotnet = imports.RootElement.GetProperty("imports").GetProperty("./_framework/dotnet.js").GetString()!;
        Assert.Matches(@"^\./_framework/dotnet\.[a-z0-9]+\.js$", dotnet);
        Assert.Contains($"href=\"{dotnet[2..]}\" rel=\"preload\"", firstBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IslandPage_DoesNotHandTheResourceCollectionToTheClient()
    {
        using HttpClient client = Bare(site.Factory);

        (HttpResponseMessage response, string body) = await GetAsync(client, PracticePath);
        using HttpResponseMessage disposable = response;

        // The islands never call Assets[...]; loading the collection blocked their first render for seconds on a slow
        // phone (docs/performance.md). The server still fingerprints its own URLs and the import map.
        Match state = WebAssemblyState().Match(body);
        Assert.True(state.Success, "The page persists no WebAssembly component state.");
        using JsonDocument outer = JsonDocument.Parse(Convert.FromBase64String(state.Groups["state"].Value));
        foreach (JsonProperty entry in outer.RootElement.EnumerateObject())
        {
            string value = Encoding.UTF8.GetString(Convert.FromBase64String(entry.Value.GetString() ?? string.Empty));
            Assert.DoesNotContain("resource-collection", value, StringComparison.Ordinal);
        }

        // The import map may still name the module; only the persisted URL makes the runtime load it.
        Assert.DoesNotMatch("<script[^>]*resource-collection", body);
    }

    [Fact]
    public async Task IslandPage_IsNoindexAndNotOutputCached()
    {
        CountingOutputCacheStore store = new();
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions
        {
            ConfigureServices = services => services.Replace(ServiceDescriptor.Singleton<IOutputCacheStore>(store)),
        });
        await factory.WaitUntilReadyAsync();
        using HttpClient client = Bare(factory);

        (HttpResponseMessage first, string body) = await GetAsync(client, PracticePath);
        using HttpResponseMessage firstDisposable = first;
        (HttpResponseMessage second, _) = await GetAsync(client, PracticePath);
        using HttpResponseMessage secondDisposable = second;

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("<meta name=\"robots\" content=\"noindex\"", body, StringComparison.Ordinal);
        Assert.Equal(0, store.Writes);
        Assert.False(second.Headers.Contains("Age"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IslandPage_IsNeverCompressed(bool signedIn)
    {
        using HttpClient client = signedIn ? Browser(site.Factory) : Bare(site.Factory);
        if (signedIn)
        {
            using HttpResponseMessage callback = await TelegramSignInDriver.SignInAsync(
                client, site.Telegram, new FakeTelegramIdentity(561_203L, "Compressed Never", null), "/en", Ct);
            Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        }

        using HttpRequestMessage request = new(HttpMethod.Get, PracticePath);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "br, gzip");
        using HttpResponseMessage response = await client.SendAsync(request, Ct);
        string body = await response.Content.ReadAsStringAsync(Ct);

        // The framework persists an antiforgery token for every WebAssembly component, signed in or not.
        Assert.Contains("Blazor-WebAssembly-Component-State", body, StringComparison.Ordinal);
        Assert.Empty(response.Content.Headers.ContentEncoding);
    }

    [Fact]
    public async Task IslandPage_UnknownStack_IsNotFound()
    {
        using HttpClient client = Bare(site.Factory);

        (HttpResponseMessage response, string body) = await GetAsync(client, "/en/no-such-stack/practice");
        using HttpResponseMessage disposable = response;

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("blazor.web.js", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LanguageSwitcher_LinksDisableEnhancedNav()
    {
        using HttpClient client = Bare(site.Factory);

        foreach (string path in new[] { "/ru", PracticePath })
        {
            (HttpResponseMessage response, string body) = await GetAsync(client, path);
            using HttpResponseMessage disposable = response;

            Assert.Matches("<a href=\"/lang/[a-z]{2}\\?return=[^\"]+\" rel=\"nofollow\" hreflang=\"[a-z]{2}\" lang=\"[a-z]{2}\" data-enhance-nav=\"false\">", body);
        }
    }

    [Fact]
    public async Task IslandPage_LinksToTheSameModeInBothCultures()
    {
        using HttpClient client = Bare(site.Factory);

        (HttpResponseMessage response, string body) = await GetAsync(client, "/en/dotnet/practice?topic=ASYNC&level=senior&level=junior&utm_source=x");
        using HttpResponseMessage disposable = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("href=\"/lang/ru?return=%2Fru%2Fdotnet%2Fpractice%3Flevel%3Djunior%26level%3Dsenior%26topic%3Dasync\"", body, StringComparison.Ordinal);
        Assert.Contains("href=\"/en/signin?returnUrl=%2Fen%2Fdotnet%2Fpractice%3Flevel%3Djunior%26level%3Dsenior%26topic%3Dasync\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("return=%2Fru%2Fdotnet%2Fpractice%3Ftopic", body, StringComparison.Ordinal);
        Assert.DoesNotContain("utm_source%3Dx", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TopicTitles_LiveTopicsInCulture()
    {
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres);
        await factory.WaitUntilReadyAsync();
        await using (TrainerDbContext db = PostgresFixture.CreateContext(factory.ConnectionString))
        {
            await db.Database.ExecuteSqlAsync($"UPDATE topics SET retired_at = now() WHERE key = 'generics'", Ct);
        }

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        IStackQueries stacks = scope.ServiceProvider.GetRequiredService<IStackQueries>();

        IReadOnlyList<TopicTitle> ru = await stacks.GetTopicTitlesAsync(new CultureKey("ru"), new StackKey("dotnet"), Ct);
        IReadOnlyList<TopicTitle> en = await stacks.GetTopicTitlesAsync(new CultureKey("en"), new StackKey("dotnet"), Ct);

        // Ordered like the catalog's topic facets: by title in the culture's collation, then by key.
        StringComparer russian = StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("ru"), ignoreCase: false);
        Assert.Equal(["async / await", "Многопоточность", "Язык C#"], ru.Select(t => t.Title).Order(StringComparer.Ordinal));
        Assert.Equal(ru.Select(t => t.Title).Order(russian), ru.Select(t => t.Title));
        Assert.Equal(["async", "concurrency", "csharp"], ru.Select(t => t.Key.Value).Order(StringComparer.Ordinal));
        Assert.Equal(["async / await", "C# language", "Multithreading"], en.Select(t => t.Title));
        Assert.Equal(["async", "csharp", "concurrency"], en.Select(t => t.Key.Value));
    }

    [Fact]
    public async Task Bootstrap_SignedIn_HasMe_AnonymousHasNull()
    {
        const long telegramId = 561_201L;
        await using AsyncServiceScope scope = site.Factory.Services.CreateAsyncScope();
        IslandBootstrapFactory bootstrap = scope.ServiceProvider.GetRequiredService<IslandBootstrapFactory>();
        UserAccess access = await scope.ServiceProvider.GetRequiredService<IUserAccountService>()
            .SignInAsync(new TelegramProfile(telegramId, "boot_user", "Boot User"), Ct);
        ClaimsPrincipal signedIn = new(new ClaimsIdentity(
            [.. new UserClaimsFactory().Create(access), new Claim(TrainerClaimTypes.TelegramUserId, telegramId.ToString(System.Globalization.CultureInfo.InvariantCulture))],
            TelegramAuth.CookieScheme));
        PracticeFiltersDto filters = new(["junior"], ["async"], [], "all", null);

        IslandBootstrapDto anonymous = Parse(await bootstrap.CreateJsonAsync(
            new CultureKey("en"), new StackKey("dotnet"), filters, new ClaimsPrincipal(new ClaimsIdentity()), Ct));
        IslandBootstrapDto user = Parse(await bootstrap.CreateJsonAsync(new CultureKey("ru"), new StackKey("dotnet"), filters, signedIn, Ct));

        Assert.Null(anonymous.Me);
        Assert.Equal("en", anonymous.Culture);
        Assert.Equal("dotnet", anonymous.Stack);
        Assert.Equal(filters.Levels, anonymous.InitialFilters.Levels);
        Assert.Equal(filters.Topics, anonymous.InitialFilters.Topics);
        Assert.Equal(["junior", "middle", "senior", "lead", "architect"], anonymous.Levels);
        Assert.Equal(["open", "single", "multi", "output", "find-bug", "situation"], anonymous.Types);
        Assert.Contains(anonymous.Topics, t => t.Key == "csharp" && t.Title == "C# language");

        MeDto me = Assert.IsType<MeDto>(user.Me);
        Assert.Equal(telegramId, me.TelegramUserId);
        Assert.Equal(0, me.DueNow);
        Assert.Contains(user.Topics, t => t.Key == "csharp" && t.Title == "Язык C#");
    }

    [Fact]
    public async Task IslandPage_SerialisesAuthStateWithCustomClaimsOnly()
    {
        using HttpClient client = Browser(site.Factory);
        using HttpResponseMessage callback = await TelegramSignInDriver.SignInAsync(
            client, site.Telegram, new FakeTelegramIdentity(561_202L, "Island Reader", "island_reader"), "/en", Ct);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);

        (HttpResponseMessage response, string body) = await GetAsync(client, PracticePath);
        using HttpResponseMessage disposable = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AuthenticationStateData state = PersistedAuthenticationState(body);
        Assert.Equal("name", state.NameClaimType);
        Assert.Equal(
            [("name", "Island Reader"), (TrainerClaimTypes.TelegramUserId, "561202")],
            state.Claims.Select(c => (c.Type, c.Value)).OrderBy(c => c.Type, StringComparer.Ordinal));
        Assert.DoesNotContain(TrainerClaimTypes.SecurityStamp, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticationStateSerialization_SendsTelegramIdNameAndRolesOnly()
    {
        ClaimsPrincipal user = new(new ClaimsIdentity(
            [
                new Claim(TrainerClaimTypes.TelegramUserId, "42"),
                new Claim(TrainerClaimTypes.Username, "only_username"),
                new Claim(TrainerClaimTypes.UserId, "01JZZZZZZZZZZZZZZZZZZZZZZZ"),
                new Claim(TrainerClaimTypes.SecurityStamp, "stamp"),
                new Claim(TrainerClaimTypes.Subject, "opaque-sub"),
                new Claim(ClaimTypes.Role, "admin"),
                new Claim("picture", "https://example.test/p.jpg"),
            ],
            TelegramAuth.CookieScheme));
        ClaimsPrincipal nameless = new(new ClaimsIdentity([new Claim(TrainerClaimTypes.TelegramUserId, "43")], TelegramAuth.CookieScheme));

        AuthenticationStateData? data = await AuthenticationStateSerialization.SerializeAsync(new AuthenticationState(user));
        AuthenticationStateData? noName = await AuthenticationStateSerialization.SerializeAsync(new AuthenticationState(nameless));
        AuthenticationStateData? anonymous = await AuthenticationStateSerialization.SerializeAsync(
            new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));

        Assert.NotNull(data);
        Assert.Equal("name", data.NameClaimType);
        Assert.Equal(ClaimTypes.Role, data.RoleClaimType);
        Assert.Equal(
            [(ClaimTypes.Role, "admin"), ("name", "only_username"), (TrainerClaimTypes.TelegramUserId, "42")],
            data.Claims.Select(c => (c.Type, c.Value)).OrderBy(c => c.Type, StringComparer.Ordinal));
        Assert.Equal([(TrainerClaimTypes.TelegramUserId, "43")], noName!.Claims.Select(c => (c.Type, c.Value)));
        Assert.Null(anonymous);
    }

    [Fact]
    public async Task Robots_DisallowsProfile()
    {
        using HttpClient client = Bare(site.Factory);

        (HttpResponseMessage response, string body) = await GetAsync(client, "/robots.txt");
        using HttpResponseMessage disposable = response;

        string[] lines = body.Split(["\r\n", "\n"], StringSplitOptions.None);
        Assert.Contains("Disallow: /ru/*/profile", lines);
        Assert.Contains("Disallow: /en/*/profile", lines);
        Assert.Contains("Disallow: /en/*/practice", lines);
    }

    private static IslandBootstrapDto Parse(string json) =>
        JsonSerializer.Deserialize(json, TrainerJsonContext.Default.IslandBootstrapDto)
        ?? throw new InvalidOperationException("The bootstrap JSON is null.");

    // The server persists component state for WebAssembly as base64 JSON of key → base64 JSON value; the auth state is
    // the value that has claims, whatever key the framework version gives it.
    private static AuthenticationStateData PersistedAuthenticationState(string body)
    {
        Match match = WebAssemblyState().Match(body);
        Assert.True(match.Success, "The page persists no WebAssembly component state.");
        using JsonDocument outer = JsonDocument.Parse(Convert.FromBase64String(match.Groups["state"].Value));
        foreach (JsonProperty entry in outer.RootElement.EnumerateObject())
        {
            byte[] value = Convert.FromBase64String(entry.Value.GetString() ?? string.Empty);
            string json = Encoding.UTF8.GetString(value);
            if (json.Contains("\"claims\"", StringComparison.OrdinalIgnoreCase))
            {
                return JsonSerializer.Deserialize<AuthenticationStateData>(json, JsonSerializerOptions.Web)
                    ?? throw new InvalidOperationException("The persisted auth state is null.");
            }
        }

        throw new InvalidOperationException("The persisted state has no authentication state.");
    }
}
