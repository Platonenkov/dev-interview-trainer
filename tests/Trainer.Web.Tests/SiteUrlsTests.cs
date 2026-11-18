using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Trainer.Web.Seo;
using Trainer.Web.Startup;

namespace Trainer.Web.Tests;

public sealed class SiteUrlsTests
{
    private static SiteUrls Urls(string? baseUrl, SiteEdition edition)
    {
        IOptions<AppOptions> options = Microsoft.Extensions.Options.Options.Create(new AppOptions { PublicBaseUrl = baseUrl, Edition = edition });
        return new SiteUrls(options, new SitePaths(options));
    }

    [Theory]
    [InlineData("https://h.io/dit/", "https://h.io")]
    [InlineData("https://h.io", "https://h.io")]
    [InlineData("https://h.io:8443/dit", "https://h.io:8443")]
    [InlineData("https://H.IO:443", "https://H.IO:443")]
    [InlineData("https://H.IO:443/", "https://H.IO:443")]
    public void Origin_IsSchemeHostAndPortOnly(string baseUrl, string expected) =>
        Assert.Equal(expected, Urls(baseUrl, SiteEdition.Static).Origin(null));

    [Fact]
    public void Absolute_AddsTheOriginToAPathAwarePage()
    {
        Assert.Equal("https://h.io/dit/ru/dotnet/", Urls("https://h.io/dit/", SiteEdition.Static).Absolute(null, "/ru/dotnet"));
        Assert.Equal("https://h.io/dit/ru/dotnet", Urls("https://h.io/dit/", SiteEdition.Server).Absolute(null, "/ru/dotnet"));
    }

    [Fact]
    public void Origin_FallsBackToTheRequestWhenNothingIsConfigured()
    {
        DefaultHttpContext context = new();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost", 5000);

        Assert.Equal("http://localhost:5000", Urls(null, SiteEdition.Server).Origin(context.Request));
        Assert.Equal(string.Empty, Urls(null, SiteEdition.Server).Origin(null));
    }
}
