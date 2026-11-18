using System.Web;
using Trainer.Contracts;

namespace Trainer.Application.Tests;

public sealed class ProblemReportLinkTests
{
    private const string IssuesNew = "https://github.com/o/r/issues/new";

    private static System.Collections.Specialized.NameValueCollection Query(string url) =>
        HttpUtility.ParseQueryString(new Uri(url).Query);

    [Fact]
    public void Build_NamesTheTemplateAndPrefillsIdLanguageAndPage()
    {
        string url = ProblemReportLink.Build(IssuesNew, "q-123", "closure-for-loop", "en", "https://h.io/dit/en/dotnet/questions/closure-for-loop/");

        Assert.StartsWith(IssuesNew + "?", url, StringComparison.Ordinal);
        System.Collections.Specialized.NameValueCollection query = Query(url);
        Assert.Equal("question-report.yml", query["template"]);
        Assert.Equal("[en] closure-for-loop", query["title"]);
        Assert.Equal("q-123", query["question-id"]);
        Assert.Equal("en", query["language"]);
        Assert.Equal("https://h.io/dit/en/dotnet/questions/closure-for-loop/", query["page-url"]);
        Assert.Equal(5, query.Count);
        // The label lives in the issue template: GitHub ignores labels= for users without triage rights.
        Assert.DoesNotContain("labels=", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_EscapesEveryValue()
    {
        const string id = "a&b=c#d e";
        const string slug = "x&labels=bug#y z";
        const string culture = "r&u";
        const string page = "https://h.io/дит/q?a=1&b=2#frag";

        string url = ProblemReportLink.Build(IssuesNew, id, slug, culture, page);

        Uri parsed = new(url);
        Assert.Equal(string.Empty, parsed.Fragment);
        System.Collections.Specialized.NameValueCollection query = Query(url);
        Assert.Equal(["template", "title", "question-id", "language", "page-url"], query.AllKeys);
        Assert.Equal(id, query["question-id"]);
        Assert.Equal($"[{culture}] {slug}", query["title"]);
        Assert.Equal(culture, query["language"]);
        Assert.Equal(page, query["page-url"]);
    }

    [Fact]
    public void Build_TruncatesALongTitleAndKeepsTheUrlWithinTheCap()
    {
        string slug = new('s', 5000);

        string url = ProblemReportLink.Build(IssuesNew, "q", slug, "en", "https://h.io/p/");

        Assert.True(url.Length <= ProblemReportLink.MaxUrlLength);
        string title = Query(url)["title"]!;
        Assert.StartsWith("[en] ", title, StringComparison.Ordinal);
        Assert.True(title.Length <= ProblemReportLink.MaxTitleLength);
    }

    [Fact]
    public void Build_NeverSplitsASurrogatePairWhenTruncating()
    {
        string slug = new string('a', ProblemReportLink.MaxTitleLength - 5 - 1) + "\U0001F600\U0001F600";

        string title = Query(ProblemReportLink.Build(IssuesNew, "q", slug, "en", "https://h.io/p/"))["title"]!;

        Assert.False(char.IsHighSurrogate(title[^1]));
    }

    [Fact]
    public void Build_RejectsValuesThatCannotFitTheUrlCap()
    {
        string page = "https://h.io/" + new string('p', ProblemReportLink.MaxUrlLength);

        Assert.Throws<ArgumentException>(() => ProblemReportLink.Build(IssuesNew, "q", "s", "en", page));
    }

    [Fact]
    public void Build_CollapsesNewlinesAndControlCharactersInTheTitle()
    {
        string url = ProblemReportLink.Build(IssuesNew, "q", "a\r\n\n b\t\u0001c  ", "en", "https://h.io/p/");

        Assert.Equal("[en] a b c", Query(url)["title"]);
        Assert.DoesNotContain("%0A", url, StringComparison.Ordinal);
        Assert.DoesNotContain("%0D", url, StringComparison.Ordinal);
    }
}
