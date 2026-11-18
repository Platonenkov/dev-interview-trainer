using Trainer.Contracts;
using Trainer.Web.Client.Sessions;

namespace Trainer.Web.Client.Tests;

public sealed class PracticeQueryStringTests
{
    private const string FirstId = "01KBZ3Q8N9V2K3M4P5R6S7T8WA";

    private static readonly TopicDto[] Topics = [new("closures", "Closures"), new("async-await", "Async"), new("linq", "LINQ")];

    [Fact]
    public void Parse_ReadsLevelTopicTypeStatusAndFirst()
    {
        PracticeFiltersDto filters = PracticeQueryString.Parse($"?level=senior&topic=linq&type=find-bug&status=due&first={FirstId}", Topics);

        Assert.Equal(["senior"], filters.Levels);
        Assert.Equal(["linq"], filters.Topics);
        Assert.Equal(["find-bug"], filters.Types);
        Assert.Equal("due", filters.Status);
        Assert.Equal(FirstId, filters.FirstQuestionId);
    }

    [Fact]
    public void Parse_NormalisesLikeTheCatalog()
    {
        PracticeFiltersDto filters = PracticeQueryString.Parse(
            "level=SENIOR&level=junior&level=junior&level=wizard&topic=LINQ&topic=Async-Await&topic=linq&topic=nope&type=open&type=MULTI&_r=123&utm_source=x",
            Topics);

        // Levels and types in domain order, topics in key order, case ignored, repeats and unknown values dropped.
        Assert.Equal(["junior", "senior"], filters.Levels);
        Assert.Equal(["async-await", "linq"], filters.Topics);
        Assert.Equal(["open", "multi"], filters.Types);
        Assert.Null(filters.Status);
        Assert.Null(filters.FirstQuestionId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?")]
    [InlineData("?_r=1739")]
    [InlineData("?status=everything")]
    [InlineData("?first=not-an-id")]
    [InlineData("?first=01kbz3q8n9v2k3m4p5r6s7t8wa")]
    public void Parse_AnythingElseLeavesTheFiltersUnset(string query)
    {
        PracticeFiltersDto filters = PracticeQueryString.Parse(query, Topics);

        Assert.Empty(filters.Levels);
        Assert.Empty(filters.Topics);
        Assert.Empty(filters.Types);
        Assert.Null(filters.Status);
        Assert.Null(filters.FirstQuestionId);
    }

    [Fact]
    public void Parse_DecodesAndIgnoresTheFragment()
    {
        PracticeFiltersDto filters = PracticeQueryString.Parse("?topic=async%2Dawait&level=mid%64le#level=senior", Topics);

        Assert.Equal(["async-await"], filters.Topics);
        Assert.Equal(["middle"], filters.Levels);
    }

    [Fact]
    public void Parse_TakesTheFirstStatusAndTheFirstFirst()
    {
        PracticeFiltersDto filters = PracticeQueryString.Parse($"status=new&status=due&first={FirstId}&first=01KBZ3Q8N9V2K3M4P5R6S7T8WB", Topics);

        Assert.Equal("new", filters.Status);
        Assert.Equal(FirstId, filters.FirstQuestionId);
    }

    [Fact]
    public void Apply_OnlyChangesTheStaticEditionsBootstrap()
    {
        IslandBootstrapDto server = Bootstrap(site: null);
        IslandBootstrapDto onStatic = Bootstrap(site: new StaticSiteDto("data/en/dotnet/index.x.json", "https://github.com/o/r/issues/new"));
        const string uri = "http://localhost/dit/en/dotnet/practice/?level=senior&status=new";

        Assert.Same(server, PracticeQueryString.Apply(server, uri, forMock: false));
        IslandBootstrapDto applied = PracticeQueryString.Apply(onStatic, uri, forMock: false);
        Assert.Equal(["senior"], applied.InitialFilters.Levels);
        Assert.Equal("new", applied.InitialFilters.Status);
        Assert.Equal(onStatic.StaticSite, applied.StaticSite);
    }

    [Fact]
    public void Apply_ForAMock_KeepsOnlyLevelsAndTopics()
    {
        IslandBootstrapDto onStatic = Bootstrap(site: new StaticSiteDto("data/en/dotnet/index.x.json", "https://github.com/o/r/issues/new"));

        IslandBootstrapDto applied = PracticeQueryString.Apply(onStatic, $"http://localhost/dit/en/dotnet/mock/?level=lead&topic=linq&type=open&status=due&first={FirstId}", forMock: true);

        Assert.Equal(["lead"], applied.InitialFilters.Levels);
        Assert.Equal(["linq"], applied.InitialFilters.Topics);
        Assert.Empty(applied.InitialFilters.Types);
        Assert.Null(applied.InitialFilters.Status);
        Assert.Null(applied.InitialFilters.FirstQuestionId);
    }

    [Fact]
    public void Apply_WithoutAQueryStringLeavesNothingSet()
    {
        IslandBootstrapDto onStatic = Bootstrap(site: new StaticSiteDto("data/en/dotnet/index.x.json", "https://github.com/o/r/issues/new"));

        IslandBootstrapDto applied = PracticeQueryString.Apply(onStatic, "http://localhost/dit/en/dotnet/practice/", forMock: false);

        Assert.Empty(applied.InitialFilters.Levels);
        Assert.Null(applied.InitialFilters.FirstQuestionId);
    }

    private static IslandBootstrapDto Bootstrap(StaticSiteDto? site) =>
        new("en", "dotnet", Topics, ["junior", "middle", "senior", "lead", "architect"], ["open", "multi", "find-bug"], new PracticeFiltersDto([], [], [], null, null), null, site);
}
