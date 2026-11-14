using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Endpoints;

namespace Trainer.Web.Islands;

/// <summary>Endpoint conventions that keep the WebAssembly runtime's footprint on island pages only.</summary>
public static class IslandEndpointConventions
{
    private const string ConfiguredRenderModesMetadataType = "Microsoft.AspNetCore.Components.Endpoints.ConfiguredRenderModesMetadata";
    private const string ResourceCollectionUrlMetadataName = "ResourceCollectionUrlMetadata";

    /// <summary>
    /// Gives every page that is not an island page an empty set of configured render modes, the state every page had
    /// before WebAssembly was added. With one configured render mode the framework persists component state on every
    /// page it renders, interactive or not, and that state includes a freshly generated antiforgery token: public pages
    /// would carry a token and set the antiforgery cookie, breaking their cookie-free, output-cached responses
    /// (spec 8.2, 8.4). With none configured it persists nothing and generates no token unless a form asks for one.
    /// </summary>
    /// <remarks>
    /// The metadata type is internal to ASP.NET Core, so it is found by name and rebuilt through its constructor. A
    /// page endpoint (one that carries the public <see cref="ComponentTypeMetadata"/>) without that metadata, or a
    /// constructor that no longer takes the render mode array, makes endpoint construction throw: a silent no-op would
    /// only surface as cookies on public pages.
    /// </remarks>
    public static void KeepRenderModesOnIslandPagesOnly(EndpointBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder.Metadata.Any(m => m is InteractiveIslandPageAttribute))
        {
            return;
        }

        bool isPage = builder.Metadata.Any(m => m is ComponentTypeMetadata);
        bool replaced = false;
        for (int i = 0; i < builder.Metadata.Count; i++)
        {
            Type type = builder.Metadata[i].GetType();
            if (!string.Equals(type.FullName, ConfiguredRenderModesMetadataType, StringComparison.Ordinal))
            {
                continue;
            }

            ConstructorInfo constructor = type.GetConstructor([typeof(IComponentRenderMode[])])
                ?? throw new InvalidOperationException(
                    $"{ConfiguredRenderModesMetadataType} has no constructor taking IComponentRenderMode[]; "
                    + "the render mode convention must be updated for this framework version.");
            builder.Metadata[i] = constructor.Invoke([Array.Empty<IComponentRenderMode>()]);
            replaced = true;
        }

        if (isPage && !replaced)
        {
            throw new InvalidOperationException(
                $"Page endpoint '{builder.DisplayName}' carries no {ConfiguredRenderModesMetadataType}; "
                + "the render mode convention must be updated for this framework version.");
        }
    }

    /// <summary>
    /// Keeps the static asset collection on the server. With its URL in an island page's persisted state, the
    /// WebAssembly runtime imports <c>_framework/resource-collection.*.js</c> and deserialises every asset of the site
    /// before it renders the first island, about a quarter of the start-up time on a slow phone
    /// (docs/performance.md). The islands never resolve assets (<c>Assets[...]</c>); the server keeps fingerprinting
    /// its own URLs and the import map from the collection itself, which this metadata does not carry.
    /// </summary>
    /// <remarks>
    /// The metadata type is internal to ASP.NET Core and found by name. If a framework update renames it, the
    /// <c>IslandHostingTests</c> and Release checks for <c>resource-collection</c> fail.
    /// </remarks>
    public static void KeepResourceCollectionOnServer(EndpointBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        for (int i = builder.Metadata.Count - 1; i >= 0; i--)
        {
            Type type = builder.Metadata[i].GetType();
            if (string.Equals(type.Name, ResourceCollectionUrlMetadataName, StringComparison.Ordinal)
                && type.Namespace?.StartsWith("Microsoft.AspNetCore.Components", StringComparison.Ordinal) == true)
            {
                builder.Metadata.RemoveAt(i);
            }
        }
    }
}
