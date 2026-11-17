using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Trainer.Web.Internal;

namespace Trainer.Web.Tests;

public sealed class InternalPortFilterTests
{
    private sealed class StatusPages : IStatusCodePagesFeature
    {
        public bool Enabled { get; set; } = true;
    }

    private static async Task<(object? Result, bool NextCalled, StatusPages Pages)> RunAsync(int localPort, string host, int configuredPort = 8081)
    {
        DefaultHttpContext http = new();
        http.Connection.LocalPort = localPort;
        http.Request.Host = new HostString(host);
        StatusPages pages = new();
        http.Features.Set<IStatusCodePagesFeature>(pages);

        bool nextCalled = false;
        InternalPortFilter filter = new(Options.Create(new InternalOptions { Port = configuredPort }));
        object? result = await filter.InvokeAsync(
            new DefaultEndpointFilterInvocationContext(http),
            _ =>
            {
                nextCalled = true;
                return ValueTask.FromResult<object?>("ok");
            });
        return (result, nextCalled, pages);
    }

    [Fact]
    public async Task PortFilter_UsesSocketPortNotHost()
    {
        (object? result, bool nextCalled, StatusPages pages) = await RunAsync(localPort: 8080, host: "x:8081");

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.False(pages.Enabled);
    }

    [Fact]
    public async Task PortFilter_MatchingSocketPort_PassesThroughWhateverTheHost()
    {
        (object? result, bool nextCalled, StatusPages pages) = await RunAsync(localPort: 8081, host: "trainer.example:443");

        Assert.True(nextCalled);
        Assert.Equal("ok", result);
        Assert.False(pages.Enabled);
    }

    [Fact]
    public async Task PortFilter_PortZeroConfigured_NeverPassesThrough()
    {
        (_, bool nextCalled, _) = await RunAsync(localPort: 0, host: "localhost", configuredPort: 0);

        Assert.False(nextCalled);
    }
}
