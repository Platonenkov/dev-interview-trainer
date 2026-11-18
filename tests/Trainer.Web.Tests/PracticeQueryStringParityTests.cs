using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Trainer.Application.Catalog;
using Trainer.Contracts;
using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;
using Trainer.Web.Catalog;
using Trainer.Web.Client.Sessions;

namespace Trainer.Web.Tests;

/// <summary>
/// The static edition reads the practice and mock filters in the browser with <see cref="PracticeQueryString"/>; the server
/// edition reads them with <see cref="CatalogQueryParser"/>. Every spelling must come out the same.
/// </summary>
public sealed class PracticeQueryStringParityTests
{
    private static readonly TopicDto[] Topics = [new("async", "Async"), new("csharp", "C#"), new("concurrency", "Concurrency"), new("linq-queries", "LINQ")];

    private static readonly CultureKey En = new("en");
    private static readonly StackKey Dotnet = new("dotnet");

    public static TheoryData<string> Queries =>
    [
        "",
        "?",
        "?level=junior",
        "?level=MIDDLE&level=middle&topic=nope&utm_source=x",
        "?type=situation&type=OPEN&type=bogus&level=senior&level=junior&topic=Csharp&topic=async&topic=csharp",
        "?level=architect&level=lead&level=senior&level=middle&level=junior",
        "?topic=ASYNC&topic=Concurrency&topic=concurrency",
        "?topic=linq%2Dqueries&topic=LINQ-QUERIES",
        "?level=mid%64le&type=fin%64-bug&type=find-bug",
        "?level=&topic=&type=",
        "?level=senior+junior&topic=async+csharp",
        "?LEVEL=senior&Topic=async&TYPE=multi",
        "?_r=1739&level=lead",
        "?level=lead&_r=1739&utm_medium=mail&page=3&q=linq",
        "?type=open&type=single&type=multi&type=output&type=find-bug&type=situation",
        "?level=junior&level=Junior&level=JUNIOR",
        "?topic=async&topic=csharp&topic=concurrency&topic=linq-queries&topic=missing",
        "?level=junior#level=senior",
        "level=senior&topic=csharp",
        "?status=due&first=01KBZ3Q8N9V2K3M4P5R6S7T8WA&level=junior",
        "?status=new",
        "?status=all&status=due",
        "?status=Due",
        "?status=everything&first=nope",
        "?first=01KBZ3Q8N9V2K3M4P5R6S7T8WA&first=01KBZ3Q8N9V2K3M4P5R6S7T8WB",
        "?first=01kbz3q8n9v2k3m4p5r6s7t8wa",
        "?first=&status=",
    ];

    [Theory]
    [MemberData(nameof(Queries))]
    public void ClientParserMatchesCatalogQueryParser(string query)
    {
        HashSet<TopicKey> known = [.. Topics.Select(t => new TopicKey(t.Key))];
        // A request never carries a fragment, so the server sees the query without it.
        int fragment = query.IndexOf('#', StringComparison.Ordinal);
        string requestQuery = fragment < 0 ? query : query[..fragment];
        CatalogQuery server = CatalogQueryParser.Parse(En, Dotnet, new QueryCollection(QueryHelpers.ParseQuery(requestQuery)), known);

        PracticeFiltersDto client = PracticeQueryString.Parse(query, Topics);

        Assert.Equal(server.Levels.Select(LevelKeys.ToKey), client.Levels);
        Assert.Equal(server.Topics.Select(t => t.Value), client.Topics);
        Assert.Equal(server.Types.Select(QuestionTypeKeys.ToKey), client.Types);

        // Status and first have no shared parser: Practice.razor reads the first value of each, so that is the rule here.
        QueryCollection request = new(QueryHelpers.ParseQuery(requestQuery));
        string? status = request["status"].FirstOrDefault() is "all" or "new" or "due" ? request["status"].FirstOrDefault() : null;
        string? first = QuestionId.TryCreate(request["first"].FirstOrDefault(), out QuestionId id) ? id.Value : null;
        Assert.Equal(status, client.Status);
        Assert.Equal(first, client.FirstQuestionId);
    }
}
