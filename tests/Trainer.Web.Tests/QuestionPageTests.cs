using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Trainer.Application.Content;
using Trainer.TestSupport;

namespace Trainer.Web.Tests;

public sealed partial class QuestionPageTests(WebSiteFixture site, PostgresFixture postgres) : IClassFixture<WebSiteFixture>
{
    [GeneratedRegex(@"class=""ref__lang""", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageMarker();

    [GeneratedRegex(@"<a\s[^>]*href=""https://[^""]*""[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex ExternalAnchor();

    private async Task<(HttpResponseMessage Response, string Body)> GetAsync(string path, TrainerWebFactory? factory = null)
    {
        using HttpClient client = (factory ?? site.Factory).CreateNonRedirectingClient();
        HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        return (response, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static async Task ImportAsync(TrainerWebFactory factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        IContentImportService import = scope.ServiceProvider.GetRequiredService<IContentImportService>();
        ContentImportResult result = await import.ImportAsync(factory.Content.Root, TestContext.Current.CancellationToken);
        Assert.Equal(ContentImportStatus.Imported, result.Status);
    }

    private async Task<TrainerWebFactory> CreateOwnFactoryAsync()
    {
        TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres);
        await factory.WaitUntilReadyAsync();
        return factory;
    }

    [Fact]
    public async Task ServerQuestionPage_IsUnchanged()
    {
        (HttpResponseMessage response, string body) = await GetAsync("/en/dotnet/questions/closure-for-loop");
        using HttpResponseMessage _ = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("issues/new", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Report a problem", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<form", body, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"feedback\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuestionPage_ExternalLinks_UseOneRelValue()
    {
        (HttpResponseMessage response, string body) = await GetAsync("/ru/dotnet/questions/result-deadlock-ui-thread");
        using HttpResponseMessage _ = response;

        MatchCollection anchors = ExternalAnchor().Matches(body);
        Assert.NotEmpty(anchors);
        Assert.All(anchors, anchor => Assert.Contains("rel=\"nofollow noopener noreferrer\"", anchor.Value, StringComparison.Ordinal));
    }

    [Fact]
    public async Task QuestionPage_RendersAnswerInsideDetails()
    {
        (HttpResponseMessage response, string body) = await GetAsync("/ru/dotnet/questions/closure-for-loop");
        using HttpResponseMessage _ = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        int details = body.IndexOf("<details class=\"answer\"", StringComparison.Ordinal);
        int summary = body.IndexOf("Показать ответ", StringComparison.Ordinal);
        int closing = body.IndexOf("</details>", details, StringComparison.Ordinal);
        Assert.True(details >= 0 && summary > details && closing > summary);
        Assert.Contains("Что выведет код с лямбдами", body, StringComparison.Ordinal);
        Assert.Contains("<pre class=\"code\">", body, StringComparison.Ordinal);
        Assert.Contains("Разбор", body[details..closing], StringComparison.Ordinal);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.DoesNotContain("<form", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QuestionPage_SingleChoice_ListsOptionsBeforeAndVerdictsInsideDetails()
    {
        (HttpResponseMessage response, string body) = await GetAsync("/en/dotnet/questions/result-deadlock-ui-thread");
        using HttpResponseMessage _ = response;

        int details = body.IndexOf("<details class=\"answer\"", StringComparison.Ordinal);
        Assert.True(details > 0);
        Assert.Contains("class=\"opts\"", body[..details], StringComparison.Ordinal);
        Assert.DoesNotContain("opt--good", body[..details], StringComparison.Ordinal);
        Assert.Contains("opt--good", body[details..], StringComparison.Ordinal);
        Assert.Contains("opt--bad", body[details..], StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuestionPage_FindBug_ShowsFixedCode()
    {
        (HttpResponseMessage response, string body) = await GetAsync("/en/dotnet/questions/parallel-counter-race");
        using HttpResponseMessage _ = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        int details = body.IndexOf("<details class=\"answer\"", StringComparison.Ordinal);
        Assert.True(details > 0);
        Assert.Contains("Fixed code", body[details..], StringComparison.Ordinal);
        Assert.Contains("Interlocked", body[details..], StringComparison.Ordinal);
        // The highlighter splits tokens into spans, so the fixed-code panel marker is the reliable signal.
        Assert.DoesNotContain("editor--fixed", body[..details], StringComparison.Ordinal);
        Assert.Contains("editor--fixed", body[details..], StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuestionPage_UnknownSlug_Returns404WithNotFoundBody()
    {
        (HttpResponseMessage response, string body) = await GetAsync("/en/dotnet/questions/no-such-question");
        using HttpResponseMessage _ = response;

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Page not found", body, StringComparison.Ordinal);
        Assert.Contains("noindex", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuestionPage_InvalidSlug_Returns404()
    {
        (HttpResponseMessage response, string body) = await GetAsync("/ru/dotnet/questions/Not_A_Slug");
        using HttpResponseMessage _ = response;

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Страница не найдена", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuestionPage_RedirectEntry_Returns301ToNewSlug()
    {
        (HttpResponseMessage response, string body) = await GetAsync("/ru/dotnet/questions/old-closure");
        using HttpResponseMessage _ = response;

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal("/ru/dotnet/questions/closure-for-loop", response.Headers.Location?.OriginalString);
        Assert.DoesNotContain("class=\"question\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetiredQuestionWithRedirect_Returns301()
    {
        await using TrainerWebFactory factory = await CreateOwnFactoryAsync();
        factory.Content.Delete("dotnet/async/to-be-retired.yaml");
        factory.Content.Write(
            "dotnet/redirects.yaml",
            "- from: csharp/old-closure\n  to: csharp/closure-for-loop\n- from: async/to-be-retired\n  to: async/configureawait-false-purpose\n");
        await ImportAsync(factory);

        (HttpResponseMessage response, _) = await GetAsync("/en/dotnet/questions/to-be-retired", factory);
        using HttpResponseMessage _1 = response;

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal("/en/dotnet/questions/configureawait-false-purpose", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task QuestionPage_MissingLocale_Returns404()
    {
        (HttpResponseMessage ru, _) = await GetAsync("/ru/dotnet/questions/en-only-question");
        (HttpResponseMessage en, _) = await GetAsync("/en/dotnet/questions/en-only-question");
        using HttpResponseMessage _1 = ru;
        using HttpResponseMessage _2 = en;

        Assert.Equal(HttpStatusCode.NotFound, ru.StatusCode);
        Assert.Equal(HttpStatusCode.OK, en.StatusCode);
    }

    [Fact]
    public async Task RetiredQuestion_IsReadableAndHiddenFromCatalog()
    {
        await using TrainerWebFactory factory = await CreateOwnFactoryAsync();
        factory.Content.Delete("dotnet/async/to-be-retired.yaml");
        await ImportAsync(factory);

        (HttpResponseMessage page, string body) = await GetAsync("/en/dotnet/questions/to-be-retired", factory);
        (HttpResponseMessage catalog, string catalogBody) = await GetAsync("/en/dotnet/questions", factory);
        using HttpResponseMessage _1 = page;
        using HttpResponseMessage _2 = catalog;

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("class=\"notice\"", body, StringComparison.Ordinal);
        Assert.Contains("has been retired", body, StringComparison.Ordinal);
        Assert.DoesNotContain("/questions/to-be-retired", catalogBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuestionPage_LinkLanguageMarkerShownWhenDifferent()
    {
        (HttpResponseMessage ru, string ruBody) = await GetAsync("/ru/dotnet/questions/configureawait-false-purpose");
        (HttpResponseMessage en, string enBody) = await GetAsync("/en/dotnet/questions/configureawait-false-purpose");
        using HttpResponseMessage _1 = ru;
        using HttpResponseMessage _2 = en;

        // The only source is an English article: marked on the Russian page, unmarked on the English one.
        Assert.Single(LanguageMarker().Matches(ruBody));
        Assert.Contains("[язык: en]", ruBody, StringComparison.Ordinal);
        Assert.Empty(LanguageMarker().Matches(enBody));
    }

    [Fact]
    public async Task QuestionPage_ShowsVerifiedDateAndDotnetVersion()
    {
        (HttpResponseMessage response, string body) = await GetAsync("/en/dotnet/questions/configureawait-false-purpose");
        using HttpResponseMessage _ = response;

        Assert.Contains("Verified 2026-11-11", body, StringComparison.Ordinal);
        Assert.Contains(".NET 10", body, StringComparison.Ordinal);
    }
}
