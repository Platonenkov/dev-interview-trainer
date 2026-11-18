using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Trainer.Web.Client.Interop;

/// <summary>Which edition of the site served the page: the dynamic server or the static GitHub Pages build.</summary>
public enum ClientEdition
{
    Server,
    Static,
}

/// <summary>
/// Reads the edition the server wrote on <c>&lt;html data-edition&gt;</c>. A missing attribute means <see cref="ClientEdition.Server"/>
/// (the server edition never writes it), so only the exact value <c>static</c> switches the client.
/// </summary>
internal static partial class ClientEditionReader
{
    public static ClientEdition Current()
    {
        if (!OperatingSystem.IsBrowser())
        {
            throw new PlatformNotSupportedException("The edition is read from the browser.");
        }

        return Parse(Read());
    }

    internal static ClientEdition Parse(string? value) =>
        string.Equals(value, "static", StringComparison.Ordinal) ? ClientEdition.Static : ClientEdition.Server;

    [SupportedOSPlatform("browser")]
    [JSImport("globalThis.ditPageEdition")]
    private static partial string Read();
}
