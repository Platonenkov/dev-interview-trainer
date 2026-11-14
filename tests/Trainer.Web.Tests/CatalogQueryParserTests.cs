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

    [Theory]
    [InlineData("?q=closure", "closure")]
    [InlineData("?q=%20%20hello%20%20%20world%09x%20", "hello world x")]
    [InlineData("?q=hello+++world", "hello world")]
    [InlineData("?q=a%00b%0D%0Ac", "a b c")]
    [InlineData("?q=one&q=two", "one")]
    [InlineData("?q=%D0%9B%D1%8F%D0%BC%D0%B1%D0%B4%D0%B0", "Лямбда")]
    [InlineData("?q=", null)]
    [InlineData("?q=%20%20%09", null)]
    [InlineData("?utm_source=x", null)]
    public void Parser_Search_TrimsCollapsesAndNormalises(string queryString, string? expected)
    {
        Assert.Equal(expected, Parse(queryString).Search);
    }

    [Fact]
    public void Parser_Search_TrimsCollapsesAndCaps()
    {
        string long150 = new('a', 150);

        Assert.Equal(new string('a', CatalogQueryParser.MaxSearchLength), Parse("?q=" + long150).Search);
        Assert.Equal(100, CatalogQueryParser.MaxSearchLength);
        Assert.Equal("q", CatalogQueryParser.SearchParameter);

        // The cut never leaves a trailing space behind.
        string spaced = new string('a', 99) + " bbb";
        Assert.Equal(new string('a', 99), Parse("?q=" + Uri.EscapeDataString(spaced)).Search);

        // Nor half of a surrogate pair, which Npgsql would refuse to encode.
        string emoji = new string('a', 99) + "\U0001F600";
        Assert.Equal(new string('a', 99), Parse("?q=" + Uri.EscapeDataString(emoji)).Search);
    }

    [Fact]
    public void Parser_ParseSearch_ReadsOnlyTheSearchTerm()
    {
        Assert.Equal("a b", CatalogQueryParser.ParseSearch(new QueryCollection(QueryHelpers.ParseQuery("?level=bogus&q=%20a%20%20b"))));
        Assert.Null(CatalogQueryParser.ParseSearch(new QueryCollection(QueryHelpers.ParseQuery("?level=middle"))));
    }

    [Fact]
    public void Canonical_IncludesSearchLast()
    {
        CatalogQuery query = Parse("?q=a%20%26%20%D0%B1&page=2&type=open&level=middle");

        Assert.Equal("level=middle&type=open&page=2&q=a%20%26%20%D0%B1", CatalogQueryParser.ToCanonicalQueryString(query));
        Assert.Equal("/ru/dotnet/questions?q=a%20%26%20%D0%B1", CatalogQueryParser.ToUrl(Parse("?q=a%20%26%20%D0%B1")));
        Assert.Equal(string.Empty, CatalogQueryParser.ToCanonicalQueryString(Parse("?q=%20")));
    }
}
