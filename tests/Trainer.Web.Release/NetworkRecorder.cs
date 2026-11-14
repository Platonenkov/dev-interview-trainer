using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Playwright;

namespace Trainer.Web.Release;

/// <summary>One request a tab made, as the DevTools Protocol saw it.</summary>
public sealed class RecordedRequest
{
    public required string RequestId { get; init; }

    public required string Url { get; init; }

    public string Path => new Uri(Url).AbsolutePath;

    /// <summary>The resource type Chromium assigned (Document, Script, Fetch, Other for prefetches...).</summary>
    public string? Type { get; init; }

    public int? Status { get; set; }

    public bool FromDiskCache { get; set; }

    public bool FromMemoryCache { get; set; }

    public bool FromPrefetchCache { get; set; }

    public bool FromCache => FromDiskCache || FromMemoryCache || FromPrefetchCache;

    /// <summary>Bytes received over the network, headers included; 0 when served from a cache.</summary>
    public long EncodedBytes { get; set; }

    public string? ContentEncoding { get; set; }

    public bool Finished { get; set; }

    public string? Failure { get; set; }
}

/// <summary>Records every request of a tab through the DevTools Protocol, including memory-cache hits and transfer sizes.</summary>
public sealed class NetworkRecorder
{
    private readonly ConcurrentDictionary<string, RecordedRequest> _requests = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<RecordedRequest> _order = new();

    private NetworkRecorder(ICDPSession session) => Session = session;

    public ICDPSession Session { get; }

    public static async Task<NetworkRecorder> AttachAsync(IBrowserContext context, IPage tab)
    {
        ArgumentNullException.ThrowIfNull(context);
        ICDPSession session = await context.NewCDPSessionAsync(tab);
        NetworkRecorder recorder = new(session);
        session.Event("Network.requestWillBeSent").OnEvent += (_, e) => recorder.OnRequest(e);
        session.Event("Network.requestServedFromCache").OnEvent += (_, e) => recorder.Update(e, r => r.FromMemoryCache = true);
        session.Event("Network.responseReceived").OnEvent += (_, e) => recorder.OnResponse(e);
        session.Event("Network.loadingFinished").OnEvent += (_, e) => recorder.Update(e, r =>
        {
            r.Finished = true;
            r.EncodedBytes = (long)e!.Value.GetProperty("encodedDataLength").GetDouble();
        });
        session.Event("Network.loadingFailed").OnEvent += (_, e) => recorder.Update(e, r => r.Failure = e!.Value.GetProperty("errorText").GetString());
        await session.SendAsync("Network.enable");
        return recorder;
    }

    /// <summary>Every request so far, in the order they were sent.</summary>
    public IReadOnlyList<RecordedRequest> Requests => [.. _order];

    /// <summary>Requests under <c>/_framework/</c>.</summary>
    public IReadOnlyList<RecordedRequest> Framework => [.. _order.Where(r => r.Path.StartsWith("/_framework/", StringComparison.Ordinal))];

    /// <summary>Forgets what was recorded, for example before the navigation that is measured.</summary>
    public void Clear()
    {
        _requests.Clear();
        while (_order.TryDequeue(out _))
        {
        }
    }

    /// <summary>Waits until every recorded request under <c>/_framework/</c> has finished or failed.</summary>
    public async Task WaitForFrameworkSettledAsync()
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (Framework.Any(r => !r.Finished && r.Failure is null))
        {
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(60), "Framework requests did not finish: " + string.Join(", ", Framework.Where(r => !r.Finished).Select(r => r.Path)));
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    private void OnRequest(JsonElement? e)
    {
        JsonElement value = e!.Value;
        string id = value.GetProperty("requestId").GetString()!;
        RecordedRequest request = new()
        {
            RequestId = id,
            Url = value.GetProperty("request").GetProperty("url").GetString()!,
            Type = value.TryGetProperty("type", out JsonElement type) ? type.GetString() : null,
        };

        // A redirect re-sends under the same id; the hop is a separate request on the wire.
        _requests[id] = request;
        _order.Enqueue(request);
    }

    private void OnResponse(JsonElement? e)
    {
        Update(e, r =>
        {
            JsonElement response = e!.Value.GetProperty("response");
            r.Status = response.GetProperty("status").GetInt32();
            r.FromDiskCache = response.TryGetProperty("fromDiskCache", out JsonElement disk) && disk.GetBoolean();
            r.FromPrefetchCache = response.TryGetProperty("fromPrefetchCache", out JsonElement prefetch) && prefetch.GetBoolean();
            if (response.TryGetProperty("headers", out JsonElement headers))
            {
                foreach (JsonProperty header in headers.EnumerateObject())
                {
                    if (string.Equals(header.Name, "content-encoding", StringComparison.OrdinalIgnoreCase))
                    {
                        r.ContentEncoding = header.Value.GetString();
                    }
                }
            }
        });
    }

    private void Update(JsonElement? e, Action<RecordedRequest> change)
    {
        string id = e!.Value.GetProperty("requestId").GetString()!;
        if (_requests.TryGetValue(id, out RecordedRequest? request))
        {
            change(request);
        }
    }
}
