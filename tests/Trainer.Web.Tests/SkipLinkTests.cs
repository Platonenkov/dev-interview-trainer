using System.Net;
using AngleSharp.Html.Parser;

namespace Trainer.Web.Tests;

public sealed class SkipLinkTests(StaticSiteFixture site) : IClassFixture<StaticSiteFixture>
{
    private async Task<string?> SkipLinkAsync(string path)
    {
        using HttpClient client = site.Factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return new HtmlParser().ParseDocument(body).QuerySelector("a.skip-link")?.GetAttribute("href");
    }

    [Fact]
    public async Task IslandPage_SkipLinkTargetsTheCurrentPrefixedPage() =>
        Assert.Equal("/dit/en/dotnet/practice/#main", await SkipLinkAsync("/dit/en/dotnet/practice/"));

    [Fact]
    public async Task IslandPage_SkipLinkKeepsTheQuery() =>
        Assert.Equal("/dit/en/dotnet/practice/?level=junior#main", await SkipLinkAsync("/dit/en/dotnet/practice/?level=junior"));

    [Fact]
    public async Task PublicPage_SkipLinkStaysAFragment() =>
        Assert.Equal("#main", await SkipLinkAsync("/dit/en"));
}
