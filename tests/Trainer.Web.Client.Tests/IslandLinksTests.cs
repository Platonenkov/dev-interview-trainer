using Microsoft.AspNetCore.Components;
using Trainer.Web.Client.Interop;
using Trainer.Web.Client.Islands;

namespace Trainer.Web.Client.Tests;

public sealed class IslandLinksTests
{
    [Fact]
    public void Page_IsBaseRelativeAndSlashedOnlyInStatic()
    {
        string staticLink = new IslandLinks(ClientEdition.Static).Page("ru", "dotnet", ["questions", "s"]);
        string serverLink = new IslandLinks(ClientEdition.Server).Page("ru", "dotnet", ["questions", "s"]);

        Assert.Equal("ru/dotnet/questions/s/", staticLink);
        Assert.Equal("ru/dotnet/questions/s", serverLink);
        Assert.False(staticLink.StartsWith('/'));
        Assert.False(serverLink.StartsWith('/'));
    }

    [Fact]
    public void PageWithQuery_IsBaseRelativeAndSlashed()
    {
        Assert.Equal("ru/dotnet/practice/?topic=async", new IslandLinks(ClientEdition.Static).Page("ru", "dotnet", ["practice"], "topic=async"));
        Assert.Equal("ru/dotnet/practice?topic=async", new IslandLinks(ClientEdition.Server).Page("ru", "dotnet", ["practice"], "topic=async"));
    }

    [Theory]
    [InlineData("static", ClientEdition.Static)]
    [InlineData("", ClientEdition.Server)]
    [InlineData(null, ClientEdition.Server)]
    [InlineData("Static", ClientEdition.Server)]
    [InlineData("server", ClientEdition.Server)]
    public void Page_EditionIsStaticOnlyForTheExactValue(string? value, ClientEdition expected) =>
        Assert.Equal(expected, ClientEditionReader.Parse(value));

    [Theory]
    [InlineData("https://h.io/dit/en/dotnet/practice/", "https://h.io/dit/en/dotnet/practice/?_r=1700000000123")]
    // The page's own query stays, and an earlier _r is replaced rather than stacked.
    [InlineData("https://h.io/dit/en/dotnet/practice/?topic=async", "https://h.io/dit/en/dotnet/practice/?topic=async&_r=1700000000123")]
    [InlineData("https://h.io/dit/en/dotnet/practice/?_r=5&topic=async", "https://h.io/dit/en/dotnet/practice/?_r=1700000000123&topic=async")]
    public void ReloadUri_KeepsThePathAndQueryAndSetsTheCacheBuster(string current, string expected)
    {
        FixedNavigation navigation = new(current);

        Assert.Equal(expected, IslandLinks.ReloadUri(navigation, DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123)));
    }

    private sealed class FixedNavigation : NavigationManager
    {
        public FixedNavigation(string uri)
        {
            int cut = uri.IndexOf("/en/", StringComparison.Ordinal) + 1;
            Initialize(uri[..cut], uri);
        }
    }
}
