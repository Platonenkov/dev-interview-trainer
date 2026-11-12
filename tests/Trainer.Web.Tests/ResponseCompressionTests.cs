using Microsoft.AspNetCore.Mvc.Testing;
using Trainer.TestSupport;

namespace Trainer.Web.Tests;

public sealed class ResponseCompressionTests(WebSiteFixture site) : IClassFixture<WebSiteFixture>
{
    private const string Path = "/en/dotnet/questions/closure-for-loop";

    private HttpClient CreateHttpsClient() => site.Factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        BaseAddress = new Uri("https://localhost"),
    });

    [Fact]
    public async Task PublicPage_OverHttps_IsCompressedWhenTheClientAcceptsBrotliOrGzip()
    {
        using HttpClient client = CreateHttpsClient();
        using HttpRequestMessage request = new(HttpMethod.Get, Path);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "br, gzip");

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("br", response.Content.Headers.ContentEncoding);
        Assert.Contains("Accept-Encoding", response.Headers.Vary);
    }

    [Fact]
    public async Task PublicPage_OverHttps_IsNotCompressedWithoutAcceptEncoding()
    {
        using HttpClient client = CreateHttpsClient();
        using HttpRequestMessage request = new(HttpMethod.Get, Path);

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Content.Headers.ContentEncoding);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("<html", body, StringComparison.Ordinal);
    }
}
