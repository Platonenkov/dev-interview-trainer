using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Trainer.Application.Catalog;
using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;
using Trainer.Web.Catalog;

namespace Trainer.Web.Tests;

public sealed class CatalogQueryParserTests
{
    private static readonly CultureKey Ru = new("ru");
    private static readonly StackKey Dotnet = new("dotnet");
    private static readonly HashSet<TopicKey> Known = [new("async"), new("csharp"), new("concurrency")];

    private static CatalogQuery Parse(string queryString) =>
        CatalogQueryParser.Parse(Ru, Dotnet, new QueryCollection(QueryHelpers.ParseQuery(queryString)), Known);

    [Fact]
    public void CatalogQueryParser_NormalisesJunkQuery()
    {
        CatalogQuery query = Parse("?level=MIDDLE&level=middle&topic=nope&utm_source=x");

        Assert.Equal([Level.Middle], query.Levels);
        Assert.Empty(query.Topics);
        Assert.Empty(query.Types);
        Assert.Equal(1, query.Page);
        Assert.Equal("level=middle", CatalogQueryParser.ToCanonicalQueryString(query));
    }

    [Fact]
    public void CatalogQueryParser_SortsValuesAndDropsUnknown()
    {
        CatalogQuery query = Parse("?type=situation&type=OPEN&type=bogus&level=senior&level=junior&topic=Csharp&topic=async&topic=csharp");

        Assert.Equal([Level.Junior, Level.Senior], query.Levels);
        Assert.Equal([QuestionType.Open, QuestionType.Situation], query.Types);
        Assert.Equal([new TopicKey("async"), new TopicKey("csharp")], query.Topics);
        Assert.Equal("level=junior&level=senior&topic=async&topic=csharp&type=open&type=situation", CatalogQueryParser.ToCanonicalQueryString(query));
    }

    [Theory]
    [InlineData("?page=0", 1)]
    [InlineData("?page=-5", 1)]
    [InlineData("?page=abc", 1)]
    [InlineData("?page=", 1)]
    [InlineData("?page=1", 1)]
    [InlineData("?page=7", 7)]
    [InlineData("?page=1000", 1000)]
    [InlineData("?page=1001", 1000)]
    [InlineData("?page=99999999999999", 1000)]
    public void CatalogQueryParser_ClampsPage(string queryString, int expected)
    {
        Assert.Equal(expected, Parse(queryString).Page);
    }

    [Fact]
    public void CatalogQueryParser_Canonical_OmitsEmptyDimensionsAndFirstPage()
    {
        Assert.Equal(string.Empty, CatalogQueryParser.ToCanonicalQueryString(Parse("?page=1&utm_source=x")));
        Assert.Equal("page=3", CatalogQueryParser.ToCanonicalQueryString(Parse("?page=3")));
    }

    [Fact]
    public void CatalogQueryParser_ToUrl_BuildsCatalogPath()
    {
        Assert.Equal("/ru/dotnet/questions", CatalogQueryParser.ToUrl(Parse("")));
        Assert.Equal("/ru/dotnet/questions?level=lead&type=find-bug", CatalogQueryParser.ToUrl(Parse("?type=Find-Bug&level=LEAD")));
    }
}
