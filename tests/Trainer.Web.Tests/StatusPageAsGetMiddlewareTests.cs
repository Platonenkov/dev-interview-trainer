using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Trainer.Web.Routing;

namespace Trainer.Web.Tests;

public sealed class StatusPageAsGetMiddlewareTests
{
    private static async Task<(string Inside, string After)> RunAsync(string method, bool reExecuted)
    {
        DefaultHttpContext context = new();
        context.Request.Method = method;
        if (reExecuted)
        {
            context.Features.Set<IStatusCodeReExecuteFeature>(new StatusCodeReExecuteFeature { OriginalPath = "/missing" });
        }

        string inside = string.Empty;
        StatusPageAsGetMiddleware middleware = new(c =>
        {
            inside = c.Request.Method;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        return (inside, context.Request.Method);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task ReExecutedWrite_RendersAsGetAndRestoresTheMethod(string method)
    {
        (string inside, string after) = await RunAsync(method, reExecuted: true);

        Assert.Equal("GET", inside);
        Assert.Equal(method, after);
    }

    [Fact]
    public async Task ReExecutedWrite_RestoresTheMethodWhenThePageThrows()
    {
        DefaultHttpContext context = new();
        context.Request.Method = "POST";
        context.Features.Set<IStatusCodeReExecuteFeature>(new StatusCodeReExecuteFeature { OriginalPath = "/missing" });
        StatusPageAsGetMiddleware middleware = new(_ => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));

        Assert.Equal("POST", context.Request.Method);
    }

    [Theory]
    [InlineData("POST", false)]
    [InlineData("GET", true)]
    [InlineData("HEAD", true)]
    public async Task OtherRequests_AreLeftAlone(string method, bool reExecuted)
    {
        (string inside, string after) = await RunAsync(method, reExecuted);

        Assert.Equal(method, inside);
        Assert.Equal(method, after);
    }
}
