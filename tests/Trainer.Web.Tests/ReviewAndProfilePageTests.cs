using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Trainer.Contracts;
using Trainer.TestSupport;
using static Trainer.Web.Tests.SignInTestHelpers;

namespace Trainer.Web.Tests;

/// <summary>
/// The Review and profile pages (spec 5.3, ADR 0010): sign-in only, island pages that are noindex and private, and the
/// anonymous-import notice that signed-in public pages carry hidden for <c>import-notice.js</c>.
/// </summary>
public sealed partial class ReviewAndProfilePageTests(SignInSiteFixture site) : IClassFixture<SignInSiteFixture>
{
    private static long _nextId = 930_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("<!--Blazor:(?<marker>\\{.*?\\})-->", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex ComponentMarker();

    [GeneratedRegex("<p class=\"import-notice\"[^>]*>.*?</p>", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex ImportNotice();

    private static long NextTelegramId() => Interlocked.Increment(ref _nextId);

    private Task<HttpClient> SignedInAsync() => ApiTestHelpers.SignInAsync(site.Factory, site.Telegram, NextTelegramId(), Ct);

    private static IslandBootstrapDto Bootstrap(string body)
    {
        Match match = ComponentMarker().Match(body);
        Assert.True(match.Success, "No WebAssembly component marker.");
        using JsonDocument marker = JsonDocument.Parse(match.Groups["marker"].Value);
        string values = Encoding.UTF8.GetString(Convert.FromBase64String(marker.RootElement.GetProperty("parameterValues").GetString()!));
        using JsonDocument parameters = JsonDocument.Parse(values);
        return JsonSerializer.Deserialize(parameters.RootElement[0].GetString()!, TrainerJsonContext.Default.IslandBootstrapDto)!;
    }

    [Fact]
    public async Task ReviewPage_Anonymous_RedirectsToSignIn()
    {
        using HttpClient client = Bare(site.Factory);

        using HttpResponseMessage response = await client.GetAsync("/ru/dotnet/review", Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/signin?ReturnUrl=%2Fru%2Fdotnet%2Freview", response.Headers.Location?.OriginalString);
        Assert.Null(SetCookie(response, ".dit.af"));
    }

    [Fact]
    public async Task ProfilePage_Anonymous_RedirectsToSignIn()
    {
        using HttpClient client = Bare(site.Factory);

        using HttpResponseMessage response = await client.GetAsync("/en/dotnet/profile", Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/signin?ReturnUrl=%2Fen%2Fdotnet%2Fprofile", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("review", "ReviewIsland", "Повторение")]
    [InlineData("profile", "ProfileIsland", "Профиль")]
    public async Task ReviewAndProfile_SignedIn_AreIslandsNoindexPrivate(string mode, string island, string heading)
    {
        using HttpClient client = await SignedInAsync();

        using HttpResponseMessage response = await client.GetAsync($"/ru/dotnet/{mode}?utm_source=x", Ct);
        string body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl is { Private: true, NoStore: true }, "A signed-in island page must be private, no-store.");
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Contains("<meta name=\"robots\" content=\"noindex\"", body, StringComparison.Ordinal);
        Assert.Contains($"<div class=\"island\" data-island=\"{mode}\">", body, StringComparison.Ordinal);
        Assert.Contains($"\"typeName\":\"Trainer.Web.Client.Islands.{island}\"", body, StringComparison.Ordinal);
        Assert.Contains("_framework/blazor.web", body, StringComparison.Ordinal);
        Assert.Contains("<base href=\"/\"", body, StringComparison.Ordinal);
        Assert.Contains($"<h1>{heading}</h1>", body, StringComparison.Ordinal);
        Assert.Contains($"<link rel=\"canonical\" href=\"https://trainer.test/ru/dotnet/{mode}\"", body, StringComparison.Ordinal);
        Assert.Contains($"hreflang=\"en\" href=\"https://trainer.test/en/dotnet/{mode}\"", body, StringComparison.Ordinal);

        // The island shows its own import notice; the server's hidden one is for public pages only.
        Assert.DoesNotMatch(ImportNotice(), body);

        IslandBootstrapDto bootstrap = Bootstrap(body);
        Assert.Equal("ru", bootstrap.Culture);
        Assert.Equal("dotnet", bootstrap.Stack);
        Assert.NotNull(bootstrap.Me);
    }

    [Fact]
    public async Task ReviewAndProfile_UnknownStack_Is404()
    {
        using HttpClient client = await SignedInAsync();

        using HttpResponseMessage review = await client.GetAsync("/en/nostack/review", Ct);
        using HttpResponseMessage profile = await client.GetAsync("/en/nostack/profile", Ct);

        Assert.Equal(HttpStatusCode.NotFound, review.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, profile.StatusCode);
    }

    [Fact]
    public async Task SignedInPage_RendersHiddenImportNotice_AnonymousPageDoesNot()
    {
        using HttpClient signedIn = await SignedInAsync();
        using HttpClient anonymous = Bare(site.Factory);

        string ru = await signedIn.GetStringAsync("/ru/about", Ct);
        string en = await signedIn.GetStringAsync("/en/dotnet", Ct);
        string anonymousPage = await anonymous.GetStringAsync("/en/dotnet", Ct);

        Match notice = ImportNotice().Match(ru);
        Assert.True(notice.Success, "A signed-in public page carries the import notice.");
        Assert.Contains(" hidden", notice.Value, StringComparison.Ordinal);
        Assert.Contains("href=\"/ru/dotnet/profile#import\"", notice.Value, StringComparison.Ordinal);
        Assert.Contains("Перенести", notice.Value, StringComparison.Ordinal);
        Assert.Matches("<script src=\"/js/import-notice[^\"]*\\.js\" defer></script>", ru);

        Match english = ImportNotice().Match(en);
        Assert.True(english.Success);
        Assert.Contains("href=\"/en/dotnet/profile#import\"", english.Value, StringComparison.Ordinal);
        Assert.Contains("Import", english.Value, StringComparison.Ordinal);

        Assert.DoesNotMatch(ImportNotice(), anonymousPage);
        Assert.DoesNotContain("import-notice", anonymousPage, StringComparison.Ordinal);
    }
}
