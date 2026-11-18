using System.IO.Compression;
using System.Net.Http.Headers;

namespace Trainer.StaticSite;

/// <param name="Url">The URL that was requested.</param>
/// <param name="Body">The response body with any content encoding removed.</param>
public sealed record FetchResult(Uri Url, int Status, byte[] Body, Uri? Location, string? MediaType);

/// <summary>
/// One GET with the retry rule of the export (503 and 429 are retried after <c>Retry-After</c>) and the decoding rule of the
/// crawler: the saved bytes are the bytes of the file, not a compressed copy, because content hashes in shard names are
/// taken over the file.
/// </summary>
public sealed class HttpFetcher(HttpClient client, Func<TimeSpan, CancellationToken, Task> delay)
{
    public const int MaxRetries = 5;
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(2);

    // A host that asks for an hour would hang the build; the export fails instead.
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(1);

    public async Task<FetchResult> GetAsync(Uri url, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            using HttpResponseMessage response = await SendAsync(request, ct);
            int status = (int)response.StatusCode;
            if (status is 503 or 429 && attempt < MaxRetries)
            {
                await delay(RetryDelay(response.Headers.RetryAfter), ct);
                continue;
            }

            byte[] raw = await response.Content.ReadAsByteArrayAsync(ct);
            byte[] body = Decode(url, response.Content.Headers.ContentEncoding, raw);
            Uri? location = response.Headers.Location is { } found ? (found.IsAbsoluteUri ? found : new Uri(url, found)) : null;
            return new FetchResult(url, status, body, location, response.Content.Headers.ContentType?.MediaType);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ExportFailedException($"GET {request.RequestUri} failed: {ex.Message}", ex);
        }
    }

    private static TimeSpan RetryDelay(RetryConditionHeaderValue? retryAfter)
    {
        TimeSpan wait = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : DefaultRetryAfter);
        return wait < TimeSpan.Zero ? TimeSpan.Zero : wait > MaxRetryAfter ? MaxRetryAfter : wait;
    }

    // The encodings come in the order they were applied, so they are undone from the end. A .br or .gz file is the file's
    // own format (the Brotli runtime ships as .br): the step that matches the name is not a transport encoding.
    private static byte[] Decode(Uri url, ICollection<string> encodings, byte[] raw)
    {
        byte[] current = raw;
        string path = url.AbsolutePath;
        foreach (string encoding in encodings.Reverse())
        {
            string name = encoding.Trim().ToLowerInvariant();
            if ((name == "br" && path.EndsWith(".br", StringComparison.Ordinal))
                || (name is "gzip" or "x-gzip" && path.EndsWith(".gz", StringComparison.Ordinal)))
            {
                break;
            }

            current = name switch
            {
                "identity" or "" => current,
                "gzip" or "x-gzip" => Inflate(current, s => new GZipStream(s, CompressionMode.Decompress)),
                "br" => Inflate(current, s => new BrotliStream(s, CompressionMode.Decompress)),
                "deflate" => Inflate(current, s => new ZLibStream(s, CompressionMode.Decompress)),
                _ => throw new ExportFailedException($"GET {url} answered with the unsupported Content-Encoding '{encoding}'."),
            };
        }

        return current;
    }

    private static byte[] Inflate(byte[] bytes, Func<Stream, Stream> wrap)
    {
        using MemoryStream source = new(bytes);
        using Stream decoder = wrap(source);
        using MemoryStream target = new();
        decoder.CopyTo(target);
        return target.ToArray();
    }
}

/// <summary>
/// Refuses every request to an origin other than the app's: the export must never contact the live site (spec 5.2), and a
/// URL that slipped through the mapping fails loudly instead of fetching.
/// </summary>
public sealed class OriginGuardHandler(HttpMessageHandler inner, Uri allowed) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { } uri || !SiteAddress.SameOrigin(uri, allowed))
        {
            throw new ExportFailedException(
                $"Refusing to fetch {request.RequestUri}: the crawler may only contact {allowed.GetLeftPart(UriPartial.Authority)}.");
        }

        return base.SendAsync(request, cancellationToken);
    }
}
