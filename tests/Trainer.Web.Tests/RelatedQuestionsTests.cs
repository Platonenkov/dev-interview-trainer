using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Trainer.Application.Content;
using Trainer.TestSupport;

namespace Trainer.Web.Tests;

public sealed partial class RelatedQuestionsTests(WebSiteFixture site, PostgresFixture postgres) : IClassFixture<WebSiteFixture>
{
    [GeneratedRegex(@"<section class=""related""[^>]*>.*?</section>", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex RelatedSection();

    [GeneratedRegex(@"<div class=""row"">", RegexOptions.CultureInvariant)]
    private static partial Regex Row();

    private async Task<(HttpResponseMessage Response, string Body)> GetAsync(string path, TrainerWebFactory? factory = null)
    {
        using HttpClient client = (factory ?? site.Factory).CreateClient();
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

    // A copy of an existing question with its own id, slug, tags and titles (both cultures).
    private static void AddQuestion(TrainerWebFactory factory, string id, string slug, string title, string tags)
    {
        string text = factory.Content.Read("dotnet/async/configureawait-false-purpose.yaml")
            .Replace("01KBZ3Q8N9V2K3M4P5R6S7T8WB", id, StringComparison.Ordinal)
            .Replace("slug: configureawait-false-purpose", "slug: " + slug, StringComparison.Ordinal)
            .Replace("tags: [configureawait, synchronization-context]", "tags: [" + tags + "]", StringComparison.Ordinal)
            .Replace("Зачем нужен ConfigureAwait(false) и когда он не нужен?", title, StringComparison.Ordinal)
            .Replace("What is ConfigureAwait(false) for, and when is it unnecessary?", title, StringComparison.Ordinal);
        factory.Content.Write($"dotnet/async/{slug}.yaml", text);
    }

    [Fact]
    public async Task QuestionPage_ShowsRelatedList()
    {
        (HttpResponseMessage response, string body) = await GetAsync("/en/dotnet/questions/result-deadlock-ui-thread");
        using HttpResponseMessage _ = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string related = RelatedSection().Match(body).Value;
        Assert.NotEmpty(related);
        Assert.Contains("Related questions", related, StringComparison.Ordinal);
        Assert.Contains("href=\"/en/dotnet/questions/configureawait-false-purpose\"", related, StringComparison.Ordinal);
        Assert.Contains("href=\"/en/dotnet/questions/en-only-question\"", related, StringComparison.Ordinal);
        Assert.DoesNotContain("questions/result-deadlock-ui-thread", related, StringComparison.Ordinal);
        Assert.DoesNotContain("questions/closure-for-loop", related, StringComparison.Ordinal);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task QuestionPage_RelatedListRespectsTheCulture()
    {
        (HttpResponseMessage response, string body) = await GetAsync("/ru/dotnet/questions/result-deadlock-ui-thread");
        using HttpResponseMessage _ = response;

        string related = RelatedSection().Match(body).Value;
        Assert.Contains("Похожие вопросы", related, StringComparison.Ordinal);
        Assert.Contains("href=\"/ru/dotnet/questions/configureawait-false-purpose\"", related, StringComparison.Ordinal);
        // The English-only question has no Russian page to link to.
        Assert.DoesNotContain("en-only-question", related, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuestionPage_WithoutSharedTags_HasNoRelatedSection()
    {
        (HttpResponseMessage response, string body) = await GetAsync("/en/dotnet/questions/closure-for-loop");
        using HttpResponseMessage _ = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("class=\"related\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Related questions", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuestionPage_RelatedList_IsCappedAtFiveAndOrderedByOverlap()
    {
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres);
        await factory.WaitUntilReadyAsync();
        // Titles starting with a digit sort before every letter in any collation. Three more questions share both tags of
        // the subject, which makes six with both tags; the one that shares a single tag has the smallest title of all.
        AddQuestion(factory, "01KBZ3Q8N9V2K3M4P5R6S7T8Y1", "rel-first", "0 first", "configureawait, synchronization-context");
        AddQuestion(factory, "01KBZ3Q8N9V2K3M4P5R6S7T8Y2", "rel-second", "0 second", "configureawait, synchronization-context");
        AddQuestion(factory, "01KBZ3Q8N9V2K3M4P5R6S7T8Y3", "rel-third", "0 third", "configureawait, synchronization-context");
        AddQuestion(factory, "01KBZ3Q8N9V2K3M4P5R6S7T8Y4", "rel-one-tag", "0 one shared tag", "configureawait");
        await ImportAsync(factory);

        (HttpResponseMessage response, string body) = await GetAsync("/en/dotnet/questions/configureawait-false-purpose", factory);
        using HttpResponseMessage _ = response;

        string related = RelatedSection().Match(body).Value;
        Assert.Equal(5, Row().Count(related));
        int first = related.IndexOf("questions/rel-first", StringComparison.Ordinal);
        int second = related.IndexOf("questions/rel-second", StringComparison.Ordinal);
        int third = related.IndexOf("questions/rel-third", StringComparison.Ordinal);
        Assert.True(first >= 0 && first < second && second < third, "Equal overlap and topic are ordered by title.");
        // More shared tags beat a smaller title: the single-tag question and the one-tag deadlock question are cut off.
        Assert.DoesNotContain("questions/rel-one-tag", related, StringComparison.Ordinal);
        Assert.DoesNotContain("questions/result-deadlock-ui-thread", related, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuestionPage_OfARetiredQuestion_HasNoRelatedList()
    {
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres);
        await factory.WaitUntilReadyAsync();
        factory.Content.Delete("dotnet/async/to-be-retired.yaml");
        await ImportAsync(factory);

        (HttpResponseMessage retired, string retiredBody) = await GetAsync("/en/dotnet/questions/to-be-retired", factory);
        (HttpResponseMessage live, string liveBody) = await GetAsync("/en/dotnet/questions/configureawait-false-purpose", factory);
        using HttpResponseMessage _1 = retired;
        using HttpResponseMessage _2 = live;

        Assert.Contains("has been retired", retiredBody, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"related\"", retiredBody, StringComparison.Ordinal);
        // The retired question also disappears from the lists of the live ones.
        Assert.Contains("class=\"related\"", liveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("questions/to-be-retired", liveBody, StringComparison.Ordinal);
    }
}
