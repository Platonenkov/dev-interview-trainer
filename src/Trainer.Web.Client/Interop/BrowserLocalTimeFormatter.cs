using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Trainer.Web.Client.Interop;

/// <summary><see cref="ILocalTimeFormatter"/> over <c>window.ditFormatTime</c> (islands-start.js), a synchronous import.</summary>
internal sealed partial class BrowserLocalTimeFormatter : ILocalTimeFormatter
{
    public string Format(string language, DateTimeOffset value, bool withTime)
    {
        if (!OperatingSystem.IsBrowser())
        {
            throw new PlatformNotSupportedException("Dates are formatted by the browser.");
        }

        return FormatTime(language, value.ToUnixTimeMilliseconds(), withTime);
    }

    [SupportedOSPlatform("browser")]
    [JSImport("globalThis.ditFormatTime")]
    private static partial string FormatTime(string language, double epochMilliseconds, bool withTime);
}
