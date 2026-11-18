using Trainer.Web.Startup;

namespace Trainer.Web.Tests;

public sealed class EditionStartupCheckTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void StaticEdition_RefusedOutsideExportDevelopmentAndTesting(string env)
    {
        AppOptions options = new() { Edition = SiteEdition.Static };

        Assert.Throws<InvalidOperationException>(() => EditionStartupCheck.EnsureValid(options, new TestHostEnvironment(env)));
    }

    [Theory]
    [InlineData(EditionStartupCheck.StaticExportEnvironment)]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void StaticEdition_AllowedInExportDevelopmentAndTesting(string env)
    {
        Exception? thrown = Record.Exception(() =>
            EditionStartupCheck.EnsureValid(new AppOptions { Edition = SiteEdition.Static }, new TestHostEnvironment(env)));

        Assert.Null(thrown);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Development")]
    public void ServerEdition_IsAllowedEverywhere(string env)
    {
        Exception? thrown = Record.Exception(() => EditionStartupCheck.EnsureValid(new AppOptions(), new TestHostEnvironment(env)));

        Assert.Null(thrown);
    }

    [Fact]
    public void Edition_DefaultsToServer() => Assert.Equal(SiteEdition.Server, new AppOptions().Edition);
}
