using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Trainer.Web.Seo;
using Trainer.Web.Startup;

namespace Trainer.Web.Tests;

public sealed class SitePathsTests
{
    private static IOptions<AppOptions> Options(string baseUrl, string edition) =>
        Microsoft.Extensions.Options.Options.Create(new AppOptions { PublicBaseUrl = baseUrl, Edition = Enum.Parse<SiteEdition>(edition) });

    private static SitePaths Paths(string baseUrl, string edition) => new(Options(baseUrl, edition));

    [Theory]
    [InlineData("https://h.io/dev-interview-trainer/", "Static", "/ru/dotnet/questions?level=junior", "/dev-interview-trainer/ru/dotnet/questions/?level=junior")]
    [InlineData("https://h.io/dev-interview-trainer", "Static", "/", "/dev-interview-trainer/")]
    [InlineData("https://h.io/", "Static", "/ru", "/ru/")]
    [InlineData("https://h.io", "Server", "/ru/dotnet", "/ru/dotnet")]
    [InlineData("https://h.io/x", "Static", "#main", "#main")]
    [InlineData("https://h.io/x", "Server", "/ru/dotnet", "/x/ru/dotnet")]
    [InlineData("https://h.io", "Server", "/", "/")]
    [InlineData("https://h.io/x", "Static", "?q=1", "?q=1")]
    [InlineData("https://h.io/x", "Static", "/ru/dotnet/", "/x/ru/dotnet/")]
    public void Page_PrefixesAndSlashesByEdition(string baseUrl, string edition, string logical, string expected) =>
        Assert.Equal(expected, Paths(baseUrl, edition).Page(logical));

    [Fact]
    public void Asset_IsPrefixedAndNeverSlashed()
    {
        SitePaths paths = Paths("https://h.io/dev-interview-trainer/", "Static");

        Assert.Equal("/dev-interview-trainer/css/s.x.css", paths.Asset("css/s.x.css"));
        Assert.Equal("/dev-interview-trainer/css/s.x.css", paths.Asset("/css/s.x.css"));
    }

    [Theory]
    [InlineData("https://h.io/dev-interview-trainer/", "/dev-interview-trainer", "/dev-interview-trainer/")]
    [InlineData("https://h.io", "", "/")]
    public void PathBase_BaseHref_AndSlashedPages(string baseUrl, string pathBase, string baseHref)
    {
        SitePaths paths = Paths(baseUrl, "Static");

        Assert.Equal(pathBase, paths.PathBase);
        Assert.Equal(baseHref, paths.BaseHref);
        Assert.True(paths.SlashedPages);
        Assert.False(Paths(baseUrl, "Server").SlashedPages);
    }

    [Fact]
    public void PathBase_IsEmptyWithoutAConfiguredUrl()
    {
        Assert.Equal(string.Empty, Paths(string.Empty, "Server").PathBase);
        Assert.Equal("/", Paths(string.Empty, "Server").BaseHref);
    }

    [Fact]
    public void CurrentPage_KeepsPathAndQueryUnderThePathBase()
    {
        DefaultHttpContext context = new();
        context.Request.Path = "/ru/dotnet/questions/";
        context.Request.QueryString = new QueryString("?level=junior");

        Assert.Equal("/dit/ru/dotnet/questions/?level=junior", Paths("https://h.io/dit", "Static").CurrentPage(context.Request));
    }

    [Fact]
    public void AbsoluteAsset_SitemapFileIsNotSlashed()
    {
        IOptions<AppOptions> options = Options("https://h.io/dit/", "Static");
        SiteUrls urls = new(options, new SitePaths(options));

        Assert.Equal("https://h.io/dit/sitemap-ru.xml", urls.AbsoluteAsset(null, "sitemap-ru.xml"));
        Assert.Equal("https://h.io/dit/sitemap-ru.xml", urls.AbsoluteAsset(null, "/sitemap-ru.xml"));
    }
}
