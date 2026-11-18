namespace Trainer.StaticSite.Tests;

public sealed class StaticSiteLayoutTests
{
    [Theory]
    [InlineData("/dit/", "/dit", "index.html")]
    [InlineData("/dit/ru/dotnet/", "/dit", "ru/dotnet/index.html")]
    [InlineData("/dit/_framework/dotnet.abc123defg.js", "/dit", "_framework/dotnet.abc123defg.js")]
    [InlineData("/", "", "index.html")]
    [InlineData("/ru/", "", "ru/index.html")]
    public void FileFor_MapsSlashedPagesToIndexHtml(string path, string pathBase, string file) =>
        Assert.Equal(file, StaticSiteLayout.FileFor(path, pathBase));

    [Theory]
    [InlineData("/other/x.js", "/dit")]
    [InlineData("/dit", "/dit")]
    [InlineData("/dit/../etc/passwd", "/dit")]
    [InlineData("/dit/a//b", "/dit")]
    [InlineData("/dit/a\\b", "/dit")]
    [InlineData("/dit/c:/x", "/dit")]
    public void FileFor_RejectsAPathThatIsNotAPlainFileUnderTheBase(string path, string pathBase) =>
        Assert.Throws<ArgumentException>(() => StaticSiteLayout.FileFor(path, pathBase));

    [Fact]
    public void Resolve_File_Is200()
    {
        StaticResolution resolution = StaticSiteLayout.Resolve("/dit/js/a.js", "/dit", f => f == "js/a.js");

        Assert.Equal(new StaticResolution(200, "js/a.js", null), resolution);
    }

    [Fact]
    public void Resolve_DirectoryWithSlash_ServesItsIndex()
    {
        StaticResolution resolution = StaticSiteLayout.Resolve("/dit/ru/", "/dit", f => f == "ru/index.html");

        Assert.Equal(new StaticResolution(200, "ru/index.html", null), resolution);
    }

    [Fact]
    public void Resolve_DirectoryWithoutSlash_Is301()
    {
        StaticResolution resolution = StaticSiteLayout.Resolve("/dit/ru", "/dit", f => f == "ru/index.html");

        Assert.Equal(301, resolution.Status);
        Assert.Equal("/dit/ru/", resolution.Location);
        Assert.Null(resolution.File);
    }

    [Fact]
    public void Resolve_PathBaseWithoutSlash_Is301ToTheSlashedRoot()
    {
        StaticResolution resolution = StaticSiteLayout.Resolve("/dit", "/dit", f => f == "index.html");

        Assert.Equal(301, resolution.Status);
        Assert.Equal("/dit/", resolution.Location);
    }

    [Fact]
    public void Resolve_Missing_Is404WithThe404Page()
    {
        StaticResolution resolution = StaticSiteLayout.Resolve("/dit/ru/missing/", "/dit", _ => false);

        Assert.Equal(404, resolution.Status);
        Assert.Equal("404.html", resolution.File);
    }

    [Theory]
    [InlineData("/ru/", "/dit")]
    [InlineData("/ditto/ru/", "/dit")]
    [InlineData("/dit/../index.html", "/dit")]
    public void Resolve_OutsideTheBaseOrTraversing_Is404(string path, string pathBase)
    {
        // exists answers yes to everything: only the layout rules may say no.
        StaticResolution resolution = StaticSiteLayout.Resolve(path, pathBase, _ => true);

        Assert.Equal(404, resolution.Status);
    }

    [Fact]
    public void Resolve_FileWithTrailingSlash_Is404()
    {
        StaticResolution resolution = StaticSiteLayout.Resolve("/dit/js/a.js/", "/dit", f => f == "js/a.js");

        Assert.Equal(404, resolution.Status);
    }
}
