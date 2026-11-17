using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Trainer.TestSupport;

/// <summary>
/// TestServer has no sockets, so <c>Connection.LocalPort</c> is 0. This copies the <see cref="Header"/> request header into
/// it ahead of the whole app pipeline, which lets a test choose the port a request "arrived" on. Registered only by
/// <see cref="TrainerWebFactory"/>, never by the application.
/// </summary>
public sealed class LocalPortFromHeaderStartupFilter : IStartupFilter
{
    public const string Header = "X-Test-Local-Port";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue(Header, out Microsoft.Extensions.Primitives.StringValues value)
                && int.TryParse(value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out int port))
            {
                context.Connection.LocalPort = port;
            }

            await nextMiddleware(context);
        });
        next(app);
    };
}
