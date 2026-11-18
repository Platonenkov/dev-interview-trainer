using Trainer.Web.Startup;

namespace Trainer.Web.Tests;

public sealed class PublicBaseUrlValidationTests
{
    [Theory]
    [InlineData("https://h.io/x?y=1")]
    [InlineData("https://h.io/x#f")]
    [InlineData("https://h.io/a/../b")]
    [InlineData("https://h.io/a/./b")]
    [InlineData("https://h.io/a/%2e%2e/b")]
    [InlineData("https://h.io/a%20b/")]
    [InlineData("https://h.io/путь/")]
    public void PublicBaseUrl_WithQueryFragmentOrDotSegments_IsRejected(string url) =>
        Assert.Throws<InvalidOperationException>(() =>
            PublicBaseUrlValidation.EnsureValid(new AppOptions { PublicBaseUrl = url }, new TestHostEnvironment("StaticExport")));

    [Theory]
    [InlineData("https://h.io")]
    [InlineData("https://h.io/")]
    [InlineData("https://h.io/dev-interview-trainer")]
    [InlineData("https://h.io/dev-interview-trainer/")]
    [InlineData("https://h.io/a/b/")]
    public void PublicBaseUrl_WithAPath_IsAccepted(string url)
    {
        Exception? thrown = Record.Exception(() =>
            PublicBaseUrlValidation.EnsureValid(new AppOptions { PublicBaseUrl = url }, new TestHostEnvironment("StaticExport")));

        Assert.Null(thrown);
    }

    [Theory]
    [InlineData("Production", null)]
    [InlineData("Production", "http://h.io")]
    [InlineData("StaticExport", "/relative")]
    public void PublicBaseUrl_OutsideDevelopmentMustBeAbsoluteHttps(string env, string? url) =>
        Assert.Throws<InvalidOperationException>(() =>
            PublicBaseUrlValidation.EnsureValid(new AppOptions { PublicBaseUrl = url }, new TestHostEnvironment(env)));

    [Fact]
    public void PublicBaseUrl_MayBeEmptyInDevelopmentAndTesting()
    {
        Exception? development = Record.Exception(() => PublicBaseUrlValidation.EnsureValid(new AppOptions(), new TestHostEnvironment("Development")));
        Exception? testing = Record.Exception(() => PublicBaseUrlValidation.EnsureValid(new AppOptions(), new TestHostEnvironment("Testing")));

        Assert.Null(development);
        Assert.Null(testing);
    }
}
