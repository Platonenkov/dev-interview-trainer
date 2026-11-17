using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Trainer.TestSupport;
using Trainer.Web.Internal;

namespace Trainer.Web.Release;

/// <summary>
/// The internal endpoints against the published build on its two real Kestrel sockets. The in-memory tests inject the
/// local port from a header, so only this proves that <c>Connection.LocalPort</c> is the real listening port.
/// </summary>
[Trait("Category", "Release")]
public sealed class InternalEndpointSocketTests(PublishedAppFixture app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Version_OnPublicSocket_Is404WithEmptyBody_AndOnInternalSocket_Is200()
    {
        app.SkipUnlessAvailable();
        using HttpClient client = new();

        using HttpResponseMessage publicResponse = await client.GetAsync(new Uri(app.BaseAddress, "internal/version"), Ct);
        using HttpResponseMessage internalResponse = await client.GetAsync(new Uri(app.InternalAddress, "internal/version"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, publicResponse.StatusCode);
        Assert.Empty(await publicResponse.Content.ReadAsByteArrayAsync(Ct));
        Assert.Null(publicResponse.Content.Headers.ContentType);
        Assert.Equal(HttpStatusCode.OK, internalResponse.StatusCode);
        InternalVersionDto version = JsonSerializer.Deserialize(
            await internalResponse.Content.ReadAsStringAsync(Ct), InternalJsonContext.Default.InternalVersionDto)!;
        Assert.Equal(1, version.ContentSchemaMin);
        Assert.Equal(1, version.ContentSchemaMax);
        Assert.False(string.IsNullOrEmpty(version.AppVersion));
    }

    [Fact]
    public async Task Import_OnPublicSocketWithInternalHostHeader_Is404()
    {
        app.SkipUnlessAvailable();
        using HttpClient client = new();

        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(app.BaseAddress, "internal/content/import"));
        request.Headers.Host = app.InternalAddress.Authority;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TrainerWebFactory.ImportToken);
        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Ct));
    }
}
