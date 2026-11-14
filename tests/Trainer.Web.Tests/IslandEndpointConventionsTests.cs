using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Trainer.Web.Islands;

namespace Trainer.Web.Tests;

public sealed class IslandEndpointConventionsTests
{
    private sealed class SamplePage : ComponentBase;

    [Fact]
    public void PageEndpointWithoutRenderModeMetadata_FailsFast()
    {
        RouteEndpointBuilder builder = new(_ => Task.CompletedTask, RoutePatternFactory.Parse("/x"), 0);
        builder.Metadata.Add(new ComponentTypeMetadata(typeof(SamplePage)));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => IslandEndpointConventions.KeepRenderModesOnIslandPagesOnly(builder));

        Assert.Contains("ConfiguredRenderModesMetadata", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonComponentEndpoint_IsLeftAlone()
    {
        RouteEndpointBuilder builder = new(_ => Task.CompletedTask, RoutePatternFactory.Parse("/x"), 0);

        IslandEndpointConventions.KeepRenderModesOnIslandPagesOnly(builder);

        Assert.Empty(builder.Metadata);
    }
}
