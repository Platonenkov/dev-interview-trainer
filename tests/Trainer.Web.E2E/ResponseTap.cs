using System.Text.Json;
using Microsoft.Playwright;

namespace Trainer.Web.E2E;

/// <summary>
/// Records the JSON bodies of one API path a tab receives, through a route: Chromium may drop the body of a fetch
/// response before the test reads it from the response event.
/// </summary>
internal sealed class ResponseTap
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly List<JsonElement> _bodies = [];

    private ResponseTap()
    {
    }

    public int Count
    {
        get
        {
            lock (_bodies)
            {
                return _bodies.Count;
            }
        }
    }

    /// <param name="glob">The route pattern, such as <c>**/api/profile*</c>.</param>
    public static async Task<ResponseTap> AttachAsync(IPage tab, string glob)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ResponseTap tap = new();
        await tab.RouteAsync(glob, tap.HandleAsync);
        return tap;
    }

    /// <summary>Waits for the <paramref name="count"/>-th body (1-based) and returns it.</summary>
    public async Task<JsonElement> WaitForAsync(int count)
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (Count < count)
        {
            Assert.True(DateTime.UtcNow < deadline, $"Response {count} did not arrive.");
            await Task.Delay(50);
        }

        lock (_bodies)
        {
            return _bodies[count - 1];
        }
    }

    private async Task HandleAsync(IRoute route)
    {
        IAPIResponse response = await route.FetchAsync();
        string body = await response.TextAsync();
        using (JsonDocument document = JsonDocument.Parse(body.Length == 0 ? "null" : body))
        {
            lock (_bodies)
            {
                _bodies.Add(document.RootElement.Clone());
            }
        }

        await route.FulfillAsync(new RouteFulfillOptions { Response = response, Body = body });
    }
}
