using System.Globalization;
using System.Net;

namespace Trainer.ContentCli.Links;

internal enum LinkStatus
{
    Ok,
    Dead,
    Unknown,
}

internal sealed record LinkCheckResult(string Url, LinkStatus Status, string Detail);

internal sealed record LinkCheckOptions
{
    public const string UserAgent = "dev-interview-trainer-link-check";

    /// <summary>Budget of one attempt for one URL, including the GET fallback and every redirect hop.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);
    public int MaxRedirects { get; init; } = 5;
    public int Concurrency { get; init; } = 4;
}

/// <summary>
/// Checks whether source links still resolve. Only a definite "gone" (404, 410, name resolution or TLS failure, a redirect
/// loop) counts as dead; anything that may be the remote side blocking or throttling a bot (401, 403, 429, timeouts, 5xx)
/// stays unknown, so a flaky publisher cannot turn a scheduled run red.
/// </summary>
internal sealed class LinkChecker(HttpClient client, LinkCheckOptions options)
{
    // Some servers reject HEAD outright (405, 501) or answer it differently from GET (403); GET is the authority.
    private static readonly HashSet<int> HeadNotSupported = [403, 405, 501];

    private sealed record Chain(int StatusCode, LinkStatus? Verdict, string Detail);

    private sealed record Attempt(LinkStatus Status, string Detail, bool Retry);

    public async Task<IReadOnlyList<LinkCheckResult>> CheckAllAsync(IEnumerable<string> urls, CancellationToken ct)
    {
        List<LinkCheckResult> results = [];
        ParallelOptions parallel = new() { MaxDegreeOfParallelism = options.Concurrency, CancellationToken = ct };
        await Parallel.ForEachAsync(urls, parallel, async (url, token) =>
        {
            LinkCheckResult result = await CheckAsync(url, token);
            lock (results)
            {
                results.Add(result);
            }
        });

        return [.. results.OrderBy(r => r.Url, StringComparer.Ordinal)];
    }

    public async Task<LinkCheckResult> CheckAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || !IsWebScheme(uri))
        {
            return new LinkCheckResult(url, LinkStatus.Dead, "not an absolute http(s) URL");
        }

        try
        {
            Attempt attempt = await AttemptAsync(uri, ct);
            if (attempt.Retry)
            {
                await Task.Delay(options.RetryDelay, ct);
                attempt = await AttemptAsync(uri, ct);
            }

            return new LinkCheckResult(url, attempt.Status, attempt.Detail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // One odd URL must not abort a scheduled run that has hundreds of others to report.
            return new LinkCheckResult(url, LinkStatus.Unknown, $"unexpected {ex.GetType().Name}");
        }
    }

    private static bool IsWebScheme(Uri uri) => uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp;

    private async Task<Attempt> AttemptAsync(Uri uri, CancellationToken ct)
    {
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(options.Timeout);
        try
        {
            Chain chain = await FollowAsync(HttpMethod.Head, uri, budget.Token);
            if (chain.Verdict is null && HeadNotSupported.Contains(chain.StatusCode))
            {
                chain = await FollowAsync(HttpMethod.Get, uri, budget.Token);
            }

            return chain.Verdict is { } verdict ? new Attempt(verdict, chain.Detail, Retry: false) : ClassifyStatus(chain.StatusCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new Attempt(LinkStatus.Unknown, "timeout", Retry: true);
        }
        catch (HttpRequestException ex)
        {
            return ClassifyFailure(ex);
        }
    }

    private async Task<Chain> FollowAsync(HttpMethod method, Uri start, CancellationToken ct)
    {
        // Redirects are followed by hand: the handler's own loop detection reports a bare exception, and the limit must be ours.
        HashSet<string> visited = new(StringComparer.Ordinal) { start.GetLeftPart(UriPartial.Query) };
        Uri current = start;
        for (int redirects = 0; ; redirects++)
        {
            using HttpRequestMessage request = new(method, current);
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            int status = (int)response.StatusCode;
            if (!IsRedirect(response.StatusCode))
            {
                return new Chain(status, null, string.Empty);
            }

            Uri? location = response.Headers.Location;
            if (location is null)
            {
                return new Chain(status, LinkStatus.Unknown, $"{status} without Location");
            }

            Uri next = location.IsAbsoluteUri ? location : new Uri(current, location);
            if (current.Scheme == Uri.UriSchemeHttps && next.Scheme == Uri.UriSchemeHttp)
            {
                // The page no longer serves TLS; readers following the citation land on an unencrypted site.
                return new Chain(status, LinkStatus.Dead, "insecure redirect (https to http)");
            }

            if (!IsWebScheme(next))
            {
                return new Chain(status, LinkStatus.Dead, $"redirect to a {next.Scheme}: URL");
            }

            if (redirects >= options.MaxRedirects)
            {
                return new Chain(status, LinkStatus.Dead, $"more than {options.MaxRedirects} redirects");
            }

            if (!visited.Add(next.GetLeftPart(UriPartial.Query)))
            {
                return new Chain(status, LinkStatus.Dead, "redirect loop");
            }

            current = next;
        }
    }

    private static bool IsRedirect(HttpStatusCode code) =>
        code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static Attempt ClassifyStatus(int status)
    {
        string detail = status.ToString(CultureInfo.InvariantCulture);
        return status switch
        {
            >= 200 and < 300 => new Attempt(LinkStatus.Ok, detail, Retry: false),
            404 or 410 => new Attempt(LinkStatus.Dead, detail, Retry: false),
            401 or 403 => new Attempt(LinkStatus.Unknown, detail, Retry: false),
            429 => new Attempt(LinkStatus.Unknown, detail, Retry: true),
            >= 400 and < 500 => new Attempt(LinkStatus.Dead, detail, Retry: false),
            >= 500 => new Attempt(LinkStatus.Unknown, detail, Retry: true),
            _ => new Attempt(LinkStatus.Unknown, detail, Retry: false),
        };
    }

    private static Attempt ClassifyFailure(HttpRequestException ex) => ex.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => new Attempt(LinkStatus.Dead, "DNS failure", Retry: true),
        HttpRequestError.SecureConnectionError => new Attempt(LinkStatus.Dead, "TLS failure", Retry: true),
        _ => new Attempt(LinkStatus.Unknown, $"network error: {ex.HttpRequestError}", Retry: true),
    };
}
