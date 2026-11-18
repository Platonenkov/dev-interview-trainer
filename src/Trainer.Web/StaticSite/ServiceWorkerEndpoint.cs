using System.Text;
using Microsoft.Extensions.FileProviders;

namespace Trainer.Web.StaticSite;

/// <summary>
/// <c>GET /sw.js</c>, mapped in the static edition only (ADR 0026): <c>js/sw-template.js</c> with the shared eligibility rule
/// prepended as <c>const ELIGIBLE = /.../;</c>. The text depends on nothing but the template and the rule, so it does not
/// change between builds unless one of them does, and the browser's byte comparison finds no new worker.
/// </summary>
public static class ServiceWorkerEndpoint
{
    public const string Route = "/sw.js";
    public const string TemplatePath = "js/sw-template.js";
    public const string ContentType = "text/javascript; charset=utf-8";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        Lazy<string> script = new(
            () => Compose(Read(endpoints.ServiceProvider.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider)),
            LazyThreadSafetyMode.ExecutionAndPublication);
        endpoints.MapGet(Route, () => Results.Text(script.Value, ContentType));
    }

    /// <summary>The worker source: the rule constant, then the template.</summary>
    public static string Compose(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return $"const ELIGIBLE = /{StaticAssetRules.JavaScriptRegex}/;\n{template.ReplaceLineEndings("\n")}";
    }

    private static string Read(IFileProvider webRoot)
    {
        IFileInfo file = webRoot.GetFileInfo(TemplatePath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("The service worker template is missing from wwwroot.", TemplatePath);
        }

        using Stream stream = file.CreateReadStream();
        using StreamReader reader = new(stream, new UTF8Encoding(false));
        return reader.ReadToEnd();
    }
}
