using Trainer.Contracts;
using Trainer.Contracts.Storage;
using Trainer.Domain.Grading;
using Trainer.Web.Client.Sessions;
using Trainer.Web.Client.Storage;

namespace Trainer.Web.Client.Tests;

public sealed class PracticeFiltersTests
{
    private static readonly string[] Levels = ["junior", "middle", "senior", "lead", "architect"];
    private static readonly string[] Types = ["open", "single", "multi", "output", "find-bug", "situation"];
    private static readonly DateTimeOffset At = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private static IslandBootstrapDto Bootstrap(PracticeFiltersDto url, bool signedIn = false) => new(
        "en",
        "dotnet",
        [new TopicDto("async", "async / await"), new TopicDto("csharp", "C# language")],
        Levels,
        Types,
        url,
        signedIn ? new MeDto(42, 1, 3, []) : null);

    private static PracticeFiltersDto NoUrl => new([], [], [], null, null);

    private static StoredPracticeFilters Remembered(string status = "new", int count = 7) =>
        new(StoredPracticeFilters.CurrentVersion, ["senior", "junior"], ["csharp"], ["single"], status, count);

    [Fact]
    public void Resolve_Nothing_GivesDefaults()
    {
        PracticeFilterSet filters = PracticeFilterSet.Resolve(Bootstrap(NoUrl), null, false);

        Assert.Empty(filters.Levels);
        Assert.Empty(filters.Topics);
        Assert.Empty(filters.Types);
        Assert.Equal(PracticeFilterSet.All, filters.Status);
        Assert.Equal(PracticeFilterSet.DefaultCount, filters.Count);
    }

    [Fact]
    public void Resolve_RememberedWinsOverDefaults_InDisplayOrder()
    {
        PracticeFilterSet filters = PracticeFilterSet.Resolve(Bootstrap(NoUrl), Remembered(), false);

        Assert.Equal(["junior", "senior"], filters.Levels);
        Assert.Equal(["csharp"], filters.Topics);
        Assert.Equal(["single"], filters.Types);
        Assert.Equal(PracticeFilterSet.New, filters.Status);
        Assert.Equal(7, filters.Count);
    }

    [Fact]
    public void Resolve_UrlFiltersWinOverRemembered_CountStillRemembered()
    {
        PracticeFilterSet filters = PracticeFilterSet.Resolve(Bootstrap(new PracticeFiltersDto([], ["async"], [], null, null)), Remembered(), false);

        Assert.Empty(filters.Levels);
        Assert.Equal(["async"], filters.Topics);
        Assert.Empty(filters.Types);
        Assert.Equal(PracticeFilterSet.All, filters.Status);
        Assert.Equal(7, filters.Count);
    }

    [Fact]
    public void Resolve_UrlStatusAlone_CountsAsUrlFilters()
    {
        PracticeFilterSet filters = PracticeFilterSet.Resolve(Bootstrap(new PracticeFiltersDto([], [], [], "all", null)), Remembered(), false);

        Assert.Empty(filters.Levels);
        Assert.Equal(PracticeFilterSet.All, filters.Status);
    }

    // "Practise this question" opens that question with the default filters behind it, not whatever was remembered
    // (a remembered architect-only filter would leave a one-question session).
    [Fact]
    public void Resolve_FirstAlone_UsesDefaultsNotRemembered_CountStillRemembered()
    {
        PracticeFilterSet filters = PracticeFilterSet.Resolve(
            Bootstrap(new PracticeFiltersDto([], [], [], null, "01KBZ3Q8N9V2K3M4P5R6S7T8WA")), Remembered(), false);

        Assert.Empty(filters.Levels);
        Assert.Empty(filters.Topics);
        Assert.Empty(filters.Types);
        Assert.Equal(PracticeFilterSet.All, filters.Status);
        Assert.Equal(7, filters.Count);
    }

    [Theory]
    [InlineData(false, "all")]
    [InlineData(true, "due")]
    public void Resolve_Due_OnlyWhenSignedIn(bool signedIn, string expected)
    {
        PracticeFilterSet filters = PracticeFilterSet.Resolve(Bootstrap(NoUrl, signedIn), Remembered(status: "due"), signedIn);

        Assert.Equal(expected, filters.Status);
    }

    [Fact]
    public void Resolve_UnknownValuesDropped_CountClamped()
    {
        StoredPracticeFilters stored = new(StoredPracticeFilters.CurrentVersion, ["middle", "guru"], ["gone"], ["essay"], "weird", 500);

        PracticeFilterSet filters = PracticeFilterSet.Resolve(Bootstrap(NoUrl), stored, false);

        Assert.Equal(["middle"], filters.Levels);
        Assert.Empty(filters.Topics);
        Assert.Empty(filters.Types);
        Assert.Equal(PracticeFilterSet.All, filters.Status);
        Assert.Equal(PracticeFilterSet.MaxCount, filters.Count);
        Assert.Equal(PracticeFilterSet.MinCount, PracticeFilterSet.Resolve(Bootstrap(NoUrl), stored with { Count = -3 }, false).Count);
    }

    [Theory]
    [InlineData("all", false, false)]
    [InlineData("new", false, false)]
    [InlineData("all", true, false)]
    [InlineData("new", true, true)]
    [InlineData("due", true, true)]
    public void ReadsServerState_OnlySignedInNewOrDue(string status, bool signedIn, bool expected)
    {
        PracticeFilterSet filters = new([], [], [], status, 20);

        Assert.Equal(expected, filters.ReadsServerState(signedIn));
    }

    [Fact]
    public void Excluded_JustSeenFirst_ThenNewestOutboxIds_DistinctAndCapped()
    {
        List<AnswerDto> outbox = [];
        for (int i = 0; i < 1200; i++)
        {
            outbox.Add(new AnswerDto(TestQuestions.NewId(), "Q" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture), "knew", false, "practice", null, At.AddMinutes(i)));
        }

        IReadOnlyList<string> excluded = PracticeFilterSet.Excluded(["S1", "S2", "Q1199", "S1"], outbox.Append(outbox[^1]).ToList());

        Assert.Equal(PracticeFilterSet.MaxExcluded, excluded.Count);
        Assert.Equal(["S1", "S2", "Q1199", "Q1198"], excluded.Take(4));
        Assert.Equal(excluded.Count, excluded.Distinct(StringComparer.Ordinal).Count());
        // The newest answers (answered last) win; the oldest question ids fall off.
        Assert.Contains("Q0202", excluded);
        Assert.DoesNotContain("Q0201", excluded);
    }

    [Fact]
    public void ToRequest_CarriesFiltersFirstAndExclusions()
    {
        PracticeFilterSet filters = new(["middle"], ["async"], ["single"], "new", 12);

        PracticeSessionRequest request = filters.ToRequest("ru", "dotnet", "01KBZ3Q8N9V2K3M4P5R6S7T8WA", ["X"]);

        Assert.Equal(("ru", "dotnet", "new", 12, "01KBZ3Q8N9V2K3M4P5R6S7T8WA"), (request.Culture, request.Stack, request.Status, request.Count, request.FirstQuestionId));
        Assert.Equal(["middle"], request.Levels);
        Assert.Equal(["async"], request.Topics);
        Assert.Equal(["single"], request.Types);
        Assert.Equal(["X"], request.ExcludeQuestionIds);
    }

    [Fact]
    public async Task Store_SaveThenLoad_RoundTrips()
    {
        FakeBrowserStore fake = new();
        PracticeFiltersStore store = new(fake);
        PracticeFilterSet filters = new(["middle"], ["async"], [], "new", 9);

        await store.SaveAsync(filters);
        StoredPracticeFilters? loaded = await new PracticeFiltersStore(fake).LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal(["middle"], loaded.Levels);
        Assert.Equal(["async"], loaded.Topics);
        Assert.Empty(loaded.Types);
        Assert.Equal("new", loaded.Status);
        Assert.Equal(9, loaded.Count);
        Assert.Equal("put", Assert.Single(fake.Ops).Op);
        Assert.Contains("\"version\":1", fake.Raw(StorageKeys.PracticeFilters), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"version\":2,\"levels\":[]}")]
    [InlineData("{\"version\":1,\"levels\":\"middle\"}")]
    public async Task Store_UnreadableValue_LoadsNothing(string raw)
    {
        FakeBrowserStore fake = new();
        fake.SetRaw(StorageKeys.PracticeFilters, raw);

        Assert.Null(await new PracticeFiltersStore(fake).LoadAsync());
    }

    [Fact]
    public void Summary_GroupsResultsByTopic_InFirstSeenOrder()
    {
        QuestionDto a = TestQuestions.Single() with { Topic = "async", TopicTitle = "async / await" };
        QuestionDto b = TestQuestions.Single() with { Topic = "csharp", TopicTitle = "C# language" };
        QuestionDto c = TestQuestions.Single() with { Topic = "async", TopicTitle = "async / await" };

        IReadOnlyList<PracticeTopicSummary> summary = PracticeSummary.ByTopic([(a, Grade.Knew), (b, Grade.DidntKnow), (c, Grade.Partial)]);

        Assert.Equal(["async", "csharp"], summary.Select(s => s.Topic));
        Assert.Equal((1, 1, 0), (summary[0].Knew, summary[0].Partial, summary[0].DidntKnow));
        Assert.Equal([a.Id, c.Id], summary[0].Questions.Select(q => q.Question.Id));
        Assert.Equal((0, 0, 1), (summary[1].Knew, summary[1].Partial, summary[1].DidntKnow));
    }
}
