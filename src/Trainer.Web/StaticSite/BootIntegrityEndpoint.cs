namespace Trainer.Web.StaticSite;

/// <summary>
/// <c>GET /js/boot-integrity.{hash}.mjs</c>, mapped in the static edition when the Brotli runtime is on (ADR 0027): the
/// <see cref="BootIntegrityModule"/>. It is generated from the endpoint manifest at startup rather than shipped as a file,
/// because only the published runtime files (trimmed, fingerprinted) have the hashes. The export crawls it like any asset.
/// </summary>
public static class BootIntegrityEndpoint
{
    public const string ContentType = "text/javascript; charset=utf-8";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        BootIntegrityModule? table = endpoints.ServiceProvider.GetRequiredService<StaticAssetList>().BootIntegrity;
        if (table is null)
        {
            return;
        }

        byte[] bytes = table.Bytes;
        endpoints.MapGet("/" + table.Route, () => Results.Bytes(bytes, ContentType));
    }
}
