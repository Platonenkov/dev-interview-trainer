using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Trainer.ContentCli.Tests;

/// <summary>A loopback Kestrel server that plays the part of third-party sites, so no test touches the internet.</summary>
internal sealed class LocalLinkServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, int> _hits = new(StringComparer.Ordinal);
    private int _flakyCalls;

    private LocalLinkServer(WebApplication app, Uri baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
    }

    public Uri BaseAddress { get; }

    public ConcurrentQueue<(string Path, Dictionary<string, string> Headers)> Requests { get; } = new();

    public Uri Url(string path) => new(BaseAddress, path);

    public int Hits(string method, string path) => _hits.GetValueOrDefault($"{method} {path}");

    public static async Task<LocalLinkServer> StartAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();
        LocalLinkServer? server = null;

        app.Use(async (context, next) =>
        {
            server!._hits.AddOrUpdate($"{context.Request.Method} {context.Request.Path}", 1, (_, count) => count + 1);
            // The header dictionary is recycled after the request, so tests get a copy.
            server.Requests.Enqueue((
                context.Request.Path.Value!,
                context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase)));
            await next();
        });

        app.MapMethods("/ok", ["GET", "HEAD"], () => Results.Ok("ok"));
        app.MapMethods("/missing", ["GET", "HEAD"], () => Results.NotFound());
        app.MapMethods("/gone", ["GET", "HEAD"], () => Results.StatusCode(StatusCodes.Status410Gone));
        app.MapMethods("/teapot", ["GET", "HEAD"], () => Results.StatusCode(StatusCodes.Status418ImATeapot));
        app.MapMethods("/forbidden", ["GET", "HEAD"], () => Results.StatusCode(StatusCodes.Status403Forbidden));
        app.MapMethods("/unauthorized", ["GET", "HEAD"], () => Results.StatusCode(StatusCodes.Status401Unauthorized));
        app.MapMethods("/busy", ["GET", "HEAD"], () => Results.StatusCode(StatusCodes.Status429TooManyRequests));
        app.MapMethods("/down", ["GET", "HEAD"], () => Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        app.MapMethods("/not-implemented", ["HEAD"], () => Results.StatusCode(StatusCodes.Status501NotImplemented));
        app.MapGet("/not-implemented", () => Results.Ok("ok"));
        app.MapMethods("/head-405", ["HEAD"], () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));
        app.MapGet("/head-405", () => Results.Ok("ok"));
        app.MapMethods("/head-405-then-missing", ["HEAD"], () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));
        app.MapGet("/head-405-then-missing", () => Results.NotFound());
        app.MapMethods("/flaky", ["GET", "HEAD"], () =>
            Interlocked.Increment(ref server!._flakyCalls) == 1 ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable) : Results.Ok("ok"));
        app.MapMethods("/slow", ["GET", "HEAD"], async (HttpContext context) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted);
            return Results.Ok("late");
        });
        app.MapMethods("/hops/{count:int}", ["GET", "HEAD"], (int count) =>
            count == 0 ? Results.Ok("ok") : Results.Redirect($"/hops/{count - 1}", permanent: count % 2 == 0));
        app.MapMethods("/cookie-start", ["GET", "HEAD"], (HttpContext context) =>
        {
            context.Response.Cookies.Append("session", "secret");
            return Results.Redirect("/cookie-next");
        });
        app.MapMethods("/cookie-next", ["GET", "HEAD"], () => Results.Ok("ok"));
        app.MapMethods("/loop/a", ["GET", "HEAD"], () => Results.Redirect("/loop/b"));
        app.MapMethods("/loop/b", ["GET", "HEAD"], () => Results.Redirect("/loop/a"));
        app.MapMethods("/to-ftp", ["GET", "HEAD"], () => Results.Redirect("ftp://example.invalid/file"));

        await app.StartAsync();
        string address = app.Urls.Single(url => url.StartsWith("http://", StringComparison.Ordinal));
        server = new LocalLinkServer(app, new Uri(address));
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
