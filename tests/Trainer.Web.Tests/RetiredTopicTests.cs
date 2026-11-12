using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Trainer.Application.Content;
using Trainer.TestSupport;

namespace Trainer.Web.Tests;

public sealed class RetiredTopicTests(PostgresFixture postgres)
{
    private const string TopicPath = "/en/dotnet/topics/generics";

    private static string WithoutGenerics(string topicsYaml)
    {
        int start = topicsYaml.IndexOf("- key: generics", StringComparison.Ordinal);
        return topicsYaml.Remove(start);
    }

    [Fact]
    public async Task RetiredQuestionOfRetiredTopic_DoesNotLinkToTheMissingTopicPage()
    {
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres);
        await factory.WaitUntilReadyAsync();
        using HttpClient client = factory.CreateNonRedirectingClient();
        const string questionPath = "/en/dotnet/questions/parallel-counter-race";
        string liveBody = await client.GetStringAsync(questionPath, TestContext.Current.CancellationToken);
        Assert.Contains("/topics/concurrency", liveBody, StringComparison.Ordinal);

        string topics = factory.Content.Read("dotnet/topics.yaml");
        int start = topics.IndexOf("- key: concurrency", StringComparison.Ordinal);
        int end = topics.IndexOf("- key: generics", StringComparison.Ordinal);
        factory.Content.Write("dotnet/topics.yaml", topics.Remove(start, end - start));
        foreach (string file in new[] { "parallel-counter-race", "thread-safe-collections", "latency-spikes-thread-pool-starvation" })
        {
            factory.Content.Delete($"dotnet/concurrency/{file}.yaml");
        }

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ContentImportResult result = await scope.ServiceProvider.GetRequiredService<IContentImportService>()
                .ImportAsync(factory.Content.Root, TestContext.Current.CancellationToken);
            Assert.Equal(ContentImportStatus.Imported, result.Status);
        }

        using HttpResponseMessage response = await client.GetAsync(questionPath, TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("class=\"notice\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("/topics/concurrency", body, StringComparison.Ordinal);
        Assert.Contains("Multithreading", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VanishedStack_BecomesNotFound_AndLeavesTheSitemap()
    {
        using ContentWorkspace content = ContentWorkspace.CopyOfWeb();
        string original = content.Read("stacks.yaml");
        const string Legacy = """
            - key: legacy
              locales:
                ru:
                  title: Legacy
                  description: Старый стек.
                en:
                  title: Legacy
                  description: An old stack.
            """;
        content.Write("stacks.yaml", original + Legacy + Environment.NewLine);
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres, new TrainerWebFactoryOptions { Content = content });
        await factory.WaitUntilReadyAsync();
        using HttpClient client = factory.CreateNonRedirectingClient();
        using (HttpResponseMessage before = await client.GetAsync("/en/legacy", TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        Assert.Contains("/en/legacy", await client.GetStringAsync("/sitemap-en.xml", TestContext.Current.CancellationToken), StringComparison.Ordinal);

        content.Write("stacks.yaml", original);
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ContentImportResult result = await scope.ServiceProvider.GetRequiredService<IContentImportService>()
                .ImportAsync(content.Root, TestContext.Current.CancellationToken);
            Assert.Equal(ContentImportStatus.Imported, result.Status);
        }

        using HttpResponseMessage after = await client.GetAsync("/en/legacy", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
        Assert.DoesNotContain("/en/legacy", await client.GetStringAsync("/sitemap-en.xml", TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Contains("/en/dotnet", await client.GetStringAsync("/sitemap-en.xml", TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VanishedTopic_BecomesNotFound_AndLeavesCatalogHomeAndSitemap()
    {
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(postgres);
        await factory.WaitUntilReadyAsync();
        using HttpClient client = factory.CreateNonRedirectingClient();

        using (HttpResponseMessage before = await client.GetAsync(TopicPath, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        string sitemapBefore = await client.GetStringAsync("/sitemap-en.xml", TestContext.Current.CancellationToken);
        Assert.Contains("/topics/concurrency", sitemapBefore, StringComparison.Ordinal);

        factory.Content.Write("dotnet/topics.yaml", WithoutGenerics(factory.Content.Read("dotnet/topics.yaml")));
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ContentImportResult result = await scope.ServiceProvider.GetRequiredService<IContentImportService>()
                .ImportAsync(factory.Content.Root, TestContext.Current.CancellationToken);
            Assert.Equal(ContentImportStatus.Imported, result.Status);
        }

        using HttpResponseMessage topic = await client.GetAsync(TopicPath, TestContext.Current.CancellationToken);
        string catalog = await client.GetStringAsync("/en/dotnet/questions?topic=generics", TestContext.Current.CancellationToken);
        string home = await client.GetStringAsync("/en", TestContext.Current.CancellationToken);
        string sitemap = await client.GetStringAsync("/sitemap-en.xml", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, topic.StatusCode);
        Assert.DoesNotContain("topic=generics", catalog, StringComparison.Ordinal);
        Assert.DoesNotContain("topics/generics", home, StringComparison.Ordinal);
        Assert.DoesNotContain("topics/generics", sitemap, StringComparison.Ordinal);
        Assert.Contains("/topics/concurrency", sitemap, StringComparison.Ordinal);
    }
}
