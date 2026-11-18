using Trainer.TestSupport;

namespace Trainer.Web.Tests;

public sealed class HomePageTests(WebSiteFixture site, PostgresFixture postgres) : IClassFixture<WebSiteFixture>
{
    private async Task<string> GetBodyAsync(string path)
    {
        using HttpClient client = site.Factory.CreateClient();
        return await client.GetStringAsync(path, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Home_ShowsStatsAndAllFiveLevels()
    {
        string body = await GetBodyAsync("/ru");

        // Web fixture, Russian: 9 questions minus the English-only one, in 3 topics, all 6 types.
        Assert.Contains("class=\"stat__value\">8<", body, StringComparison.Ordinal);
        Assert.Contains("<dt class=\"stat__label\">тем</dt><dd class=\"stat__value\">3</dd>", body, StringComparison.Ordinal);
        Assert.Contains("<dt class=\"stat__label\">типов заданий</dt><dd class=\"stat__value\">6</dd>", body, StringComparison.Ordinal);
        foreach (string level in new[] { "junior", "middle", "senior", "lead", "architect" })
        {
            Assert.Contains($"href=\"/ru/dotnet/questions?level={level}\"", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Home_ListsTopicsWithCountsAndLinks()
    {
        string body = await GetBodyAsync("/en/dotnet");

        Assert.Contains("href=\"/en/dotnet/questions?topic=async\"", body, StringComparison.Ordinal);
        Assert.Contains("title=\"async / await\"><span class=\"topic-card__title\">async / await</span> <span class=\"topic-card__count\">5</span>", body, StringComparison.Ordinal);
        Assert.Contains("<span class=\"topic-card__title\">C# language</span> <span class=\"topic-card__count\">1</span>", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Home_BareCulture_UsesConfiguredDefaultStack()
    {
        string body = await GetBodyAsync("/en");

        Assert.Contains("class=\"btn btn--primary\" href=\"/en/dotnet/questions\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Home_BareCulture_FallsBackToFirstStackWhenDefaultIsUnknown()
    {
        await using TrainerWebFactory factory = await TrainerWebFactory.CreateAsync(
            postgres,
            new TrainerWebFactoryOptions { Settings = new Dictionary<string, string?> { ["App:DefaultStack"] = "no-such-stack" } });
        await factory.WaitUntilReadyAsync();
        using HttpClient client = factory.CreateClient();

        string body = await client.GetStringAsync("/en", TestContext.Current.CancellationToken);

        Assert.Contains("href=\"/en/dotnet/questions\"", body, StringComparison.Ordinal);
    }
}
