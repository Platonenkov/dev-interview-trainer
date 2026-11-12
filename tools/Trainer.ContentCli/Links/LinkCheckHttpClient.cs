using System.Net.Http.Headers;

namespace Trainer.ContentCli.Links;

internal static class LinkCheckHttpClient
{
    /// <summary>
    /// One client per run. Cookies are off and no credentials are configured, so nothing from the machine reaches the
    /// third-party sites; redirects are followed by <see cref="LinkChecker"/> itself, and the per-URL budget replaces the client timeout.
    /// </summary>
    public static HttpClient Create()
    {
        SocketsHttpHandler handler = new()
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            PreAuthenticate = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        HttpClient client = new(handler, disposeHandler: true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(LinkCheckOptions.UserAgent, null));
        return client;
    }
}
