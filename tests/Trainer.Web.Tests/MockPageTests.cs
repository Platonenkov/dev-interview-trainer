using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Trainer.Contracts;
using Trainer.TestSupport;

namespace Trainer.Web.Tests;

/// <summary>The Mock page shell: an island page (noindex, no output cache, the runtime) with the island's bootstrap.</summary>
public sealed partial class MockPageTests(WebSiteFixture site) : IClassFixture<WebSiteFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("<!--Blazor:(?<marker>\\{.*?\\})-->", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex ComponentMarker();

    private static IslandBootstrapDto Bootstrap(string body)
    {
        Match match = ComponentMarker().Match(body);
        Assert.True(match.Success, "No WebAssembly component marker.");
        using JsonDocument marker = JsonDocument.Parse(match.Groups["marker"].Value);
        string values = Encoding.UTF8.GetString(Convert.FromBase64String(marker.RootElement.GetProperty("parameterValues").GetString()!));
        using JsonDocument parameters = JsonDocument.Parse(values);
        return JsonSerializer.Deserialize(parameters.RootElement[0].GetString()!, TrainerJsonContext.Default.IslandBootstrapDto)!;
    }

    [Fact]
    public async Task MockPage_IsIslandNoindexWithBootstrap()
    {
        using HttpClient client = site.Factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/ru/dotnet/mock?level=senior&level=junior&topic=ASYNC&type=single&utm_source=x", Ct);
        string body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<meta name=\"robots\" content=\"noindex\"", body, StringComparison.Ordinal);
        Assert.Contains("<div class=\"island\" data-island=\"mock\">", body, StringComparison.Ordinal);
        Assert.Contains("<div class=\"island-skeleton\" aria-hidden=\"true\"></div>", body, StringComparison.Ordinal);
        Assert.Contains("\"typeName\":\"Trainer.Web.Client.Islands.MockIsland\"", body, StringComparison.Ordinal);
        Assert.Contains("_framework/blazor.web", body, StringComparison.Ordinal);
        Assert.Contains("<base href=\"/\"", body, StringComparison.Ordinal);
        Assert.Contains("<html lang=\"ru\"", body, StringComparison.Ordinal);
        Assert.Contains(
            "<link rel=\"canonical\" href=\"https://trainer.test/ru/dotnet/mock?level=junior&amp;level=senior&amp;topic=async\"",
            body,
            StringComparison.Ordinal);
        Assert.Contains("hreflang=\"en\" href=\"https://trainer.test/en/dotnet/mock?level=junior&amp;level=senior&amp;topic=async\"", body, StringComparison.Ordinal);

        IslandBootstrapDto bootstrap = Bootstrap(body);
        Assert.Equal("ru", bootstrap.Culture);
        Assert.Equal("dotnet", bootstrap.Stack);
        Assert.Null(bootstrap.Me);
        Assert.Equal(["junior", "senior"], bootstrap.InitialFilters.Levels);
        Assert.Equal(["async"], bootstrap.InitialFilters.Topics);
        Assert.Empty(bootstrap.InitialFilters.Types);
        Assert.Null(bootstrap.InitialFilters.Status);
        Assert.Null(bootstrap.InitialFilters.FirstQuestionId);
        Assert.NotEmpty(bootstrap.Topics);
    }
}
