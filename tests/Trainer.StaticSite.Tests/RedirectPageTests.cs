using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace Trainer.StaticSite.Tests;

public sealed class RedirectPageTests
{
    private const string Target = "https://example.test/dit/en/dotnet/questions/new/";

    [Fact]
    public void RedirectPage_HasRefreshCanonicalNoindexLinkAndCsp()
    {
        string html = RedirectPage.Html(Target, TestPages.Csp, TestPages.BuildId);
        IHtmlDocument document = new HtmlParser().ParseDocument(html);

        IElement first = document.Head!.Children[0];
        Assert.Equal("Content-Security-Policy", first.GetAttribute("http-equiv"));
        Assert.Equal(TestPages.Csp, first.GetAttribute("content"));
        Assert.Equal("0; url=/dit/en/dotnet/questions/new/", document.QuerySelector("meta[http-equiv='refresh']")!.GetAttribute("content"));
        Assert.Equal(Target, document.QuerySelector("link[rel='canonical']")!.GetAttribute("href"));
        Assert.Equal("noindex", document.QuerySelector("meta[name='robots']")!.GetAttribute("content"));
        Assert.Equal("strict-origin-when-cross-origin", document.QuerySelector("meta[name='referrer']")!.GetAttribute("content"));
        Assert.Equal(TestPages.BuildId, document.QuerySelector("meta[name='dit-build']")!.GetAttribute("content"));
        IElement link = document.QuerySelector("body a")!;
        // Path-absolute, so a preview served from another origin stays there; only the canonical link is absolute.
        Assert.Equal("/dit/en/dotnet/questions/new/", link.GetAttribute("href"));
        Assert.Equal("/dit/en/dotnet/questions/new/", link.TextContent);
        Assert.Empty(document.QuerySelectorAll("script"));
    }

    [Fact]
    public void RedirectPage_EscapesTheTargetAndThePolicy()
    {
        string html = RedirectPage.Html("https://example.test/dit/a/?x=1&y=\"2\"<b>", "default-src 'self'; \"x\"", "id\"1");

        Assert.DoesNotContain("<b>", html, StringComparison.Ordinal);
        IHtmlDocument document = new HtmlParser().ParseDocument(html);
        string refresh = document.QuerySelector("meta[http-equiv='refresh']")!.GetAttribute("content")!;
        Assert.StartsWith("0; url=/dit/a/?x=1&y=", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain('<', refresh);
        Assert.DoesNotContain('"', refresh);
        Assert.Equal("id\"1", document.QuerySelector("meta[name='dit-build']")!.GetAttribute("content"));
        Assert.Single(document.Head!.Children, e => e.LocalName == "link");
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("/dit/en/")]
    [InlineData("https://example.test/dit/a b")]
    [InlineData("data:text/html,x")]
    [InlineData("https://example.test/dit/a\nb")]
    public void RedirectPage_RejectsATargetThatIsNotAnAbsoluteHttpUrl(string target) =>
        Assert.Throws<ArgumentException>(() => RedirectPage.Html(target, TestPages.Csp, TestPages.BuildId));

    [Fact]
    public void RedirectPage_RequiresAPolicyAndABuildId()
    {
        Assert.Throws<ArgumentException>(() => RedirectPage.Html(Target, " ", TestPages.BuildId));
        Assert.Throws<ArgumentException>(() => RedirectPage.Html(Target, TestPages.Csp, ""));
    }

    [Fact]
    public void RedirectPage_PassesTheVerifier()
    {
        using SiteDir site = SiteDir.Valid();
        site.Write("moved/index.html", RedirectPage.Html("https://example.test/dit/en/", TestPages.Csp, TestPages.BuildId));

        Assert.Empty(site.Verify());
    }
}
