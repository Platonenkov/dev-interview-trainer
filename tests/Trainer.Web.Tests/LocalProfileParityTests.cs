using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Trainer.Application.Stacks;
using Trainer.Application.StaticSite;
using Trainer.Contracts;
using Trainer.Domain.Keys;
using Trainer.Domain.Scheduling;
using Trainer.Web.Client.Backend;
using static Trainer.Web.Tests.ApiTestHelpers;

namespace Trainer.Web.Tests;

/// <summary>
/// The static edition computes the profile in the browser from the answer log (<see cref="LocalProfileBuilder"/>); the server
/// computes it in SQL (<c>ProgressQueries</c>). The same answers, recorded through the API (which uses the answer recorder),
/// must give the same rows.
/// </summary>
public sealed class LocalProfileParityTests(SignInSiteFixture site) : IClassFixture<SignInSiteFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Whole seconds: the database keeps microseconds, so a time with more digits would not survive the round trip.
    private static DateTimeOffset Ago(DateTimeOffset now, TimeSpan span) =>
        new(((now - span).UtcDateTime.Ticks / TimeSpan.TicksPerSecond) * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    [Fact]
    public async Task LocalProfileMatchesProgressQueries()
    {
        const long Owner = 650_201L;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        (StaticIndexDto index, IReadOnlyList<QuestionDto> live, IReadOnlyList<TopicDto> serverTopics) = await LoadIndexAsync();
        Assert.True(live.Count >= 5, "The fixture content needs at least five live questions.");
        string[] ids = [.. live.Take(5).Select(q => q.Id)];

        // Due and not due, known and not, a question answered twice, answers on several days; no due time sits near now.
        List<AnswerDto> answers =
        [
            Answer(ids[0], "knew", Ago(now, TimeSpan.FromDays(6))),
            Answer(ids[0], "partial", Ago(now, TimeSpan.FromDays(2))),
            Answer(ids[1], "knew", Ago(now, TimeSpan.FromHours(30))),
            Answer(ids[2], "knew", Ago(now, TimeSpan.FromHours(2))),
            Answer(ids[3], "didnt-know", Ago(now, TimeSpan.FromHours(50))),
            Answer(ids[4], "knew", Ago(now, TimeSpan.FromMinutes(10))),
        ];

        using HttpClient client = await SignInAsync(site.Factory, site.Telegram, Owner, Ct);
        using (HttpResponseMessage batch = await PostBatchAsync(client, Owner, Ct, [.. answers]))
        {
            Assert.Equal(HttpStatusCode.OK, batch.StatusCode);
            AnswerBatchResponse response = await ReadAsync(batch, TrainerJsonContext.Default.AnswerBatchResponse, Ct);
            Assert.Equal(answers.Count, response.Accepted.Count);
            Assert.Empty(response.Rejected);
        }

        using HttpResponseMessage profileResponse = await client.GetAsync("/api/profile?culture=en&stack=dotnet", Ct);
        ProfileDto server = await ReadAsync(profileResponse, TrainerJsonContext.Default.ProfileDto, Ct);

        // The server read "now" a moment after this test took it; no state is due within seconds of either.
        ProfileDto local = LocalProfileBuilder.Build(index, LocalProgress.Replay(answers, new Sm2ReviewScheduler(), now), [], DateTimeOffset.UtcNow, TimeSpan.Zero, serverTopics);

        // Unsorted: the rows must come in the order the server rendered into the island.
        Assert.Equal(server.Topics, local.Topics);
        Assert.Equal(server.Levels, local.Levels);
        Assert.Equal(server.StreakDays, local.StreakDays);
        Assert.Equal(server.DueNow, local.DueNow);
        Assert.Equal(server.TotalAnswered, local.TotalAnswered);
        Assert.Equal(server.NextDueAt, local.NextDueAt);
        Assert.True(
            server.DueNow > 0 && server.NextDueAt is not null && server.Topics.Sum(t => t.Known) > 0,
            "The scenario must exercise due, upcoming and known states.");
    }

    private async Task<(StaticIndexDto Index, IReadOnlyList<QuestionDto> Live, IReadOnlyList<TopicDto> ServerTopics)> LoadIndexAsync()
    {
        await using AsyncServiceScope scope = site.Factory.Services.CreateAsyncScope();
        CultureKey culture = new("en");
        StackKey stack = new("dotnet");
        IReadOnlyList<QuestionDto> live = await scope.ServiceProvider.GetRequiredService<IStaticSiteQueries>().GetLiveQuestionsAsync(culture, stack, Ct);
        IReadOnlyList<TopicTitle> topics = await scope.ServiceProvider.GetRequiredService<IStackQueries>().GetTopicTitlesAsync(culture, stack, Ct);
        TopicDto[] serverTopics = [.. topics.Select(t => new TopicDto(t.Key.Value, t.Title))];
        StaticIndexDto index = new(
            StaticIndexDto.CurrentFormat,
            "en",
            "dotnet",
            "parity",
            // The shard index keeps topics by key, whatever order the server renders them in.
            [.. serverTopics.OrderBy(t => t.Key, StringComparer.Ordinal)],
            [.. live.Select(q => new StaticIndexEntryDto(q.Id, q.Slug, q.Topic, q.Level, q.Type, q.Title, "0123456789abcdef"))]);
        return (index, live, serverTopics);
    }
}
