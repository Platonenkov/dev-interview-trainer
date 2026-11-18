using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Trainer.Contracts;
using Trainer.Web.Client.Api;

namespace Trainer.Web.Client.Backend;

/// <summary>
/// Fetches the static edition's content shards: the index and one file per question, named after their content hash.
/// Every path has at most one request in flight. That request belongs to this client and ends only with the page (its own
/// lifetime token), so a caller that stops waiting cancels its own wait and nothing else: a prefetch, a foreground load
/// and a second caller of the same file share one request whoever gives up. Only successes are remembered; a failed or
/// cancelled fetch is forgotten, so the next call asks again and an offline start recovers once the network is back.
/// Nothing here throws for network problems or cancellation: they come back as an <see cref="ApiResult{T}"/>.
/// </summary>
public sealed class StaticContentClient(HttpClient http) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, Task<ApiResult<StaticIndexDto>>> _indexes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<ApiResult<QuestionDto>>> _questions = new(StringComparer.Ordinal);

    /// <summary>
    /// The index at <paramref name="indexPath"/> (relative to the site's base URL). A 404 means the site was redeployed
    /// under the open page, and so does an index in a format this build does not read: both are <c>site-updated</c>.
    /// </summary>
    public async Task<ApiResult<StaticIndexDto>> GetIndexAsync(string indexPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(indexPath);
        Task<ApiResult<StaticIndexDto>> fetch = Shared(
            _indexes,
            indexPath,
            TrainerJsonContext.Default.StaticIndexDto,
            index => index.Format == StaticIndexDto.CurrentFormat ? null : SiteUpdated<StaticIndexDto>());
        return await WaitAsync(fetch, ct);
    }

    public async ValueTask<ApiResult<QuestionDto>> GetQuestionAsync(string culture, StaticIndexEntryDto entry, CancellationToken ct)
    {
        Task<ApiResult<QuestionDto>> fetch = SharedQuestion(culture, entry);
        return await WaitAsync(fetch, ct);
    }

    /// <summary>Starts loading a question nobody waits for yet; a later <see cref="GetQuestionAsync"/> joins the same request.</summary>
    public void Prefetch(string culture, StaticIndexEntryDto entry) => _ = SharedQuestion(culture, entry);

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private Task<ApiResult<QuestionDto>> SharedQuestion(string culture, StaticIndexEntryDto entry)
    {
        ArgumentException.ThrowIfNullOrEmpty(culture);
        ArgumentNullException.ThrowIfNull(entry);
        return Shared(_questions, StaticContentPaths.Question(culture, entry.Id, entry.Hash), TrainerJsonContext.Default.QuestionDto, null);
    }

    // A caller's token ends that caller's wait only; the shared request keeps running for the others.
    private static async Task<ApiResult<T>> WaitAsync<T>(Task<ApiResult<T>> fetch, CancellationToken ct)
    {
        try
        {
            return await fetch.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return NoResponse<T>();
        }
    }

    private Task<ApiResult<T>> Shared<T>(
        Dictionary<string, Task<ApiResult<T>>> cache,
        string path,
        JsonTypeInfo<T> type,
        Func<T, ApiResult<T>?>? reject)
        where T : class
    {
        lock (cache)
        {
            if (cache.TryGetValue(path, out Task<ApiResult<T>>? existing))
            {
                return existing;
            }

            Task<ApiResult<T>> fetch = FetchAsync(path, type, reject);
            cache[path] = fetch;
            // Attached after the entry exists, so a fetch that finishes at once is still evicted when it failed.
            _ = fetch.ContinueWith(
                finished =>
                {
                    if (finished.IsCompletedSuccessfully && finished.Result is { IsSuccess: true, Value: not null })
                    {
                        return;
                    }

                    lock (cache)
                    {
                        if (cache.TryGetValue(path, out Task<ApiResult<T>>? current) && ReferenceEquals(current, fetch))
                        {
                            cache.Remove(path);
                        }
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return fetch;
        }
    }

    private async Task<ApiResult<T>> FetchAsync<T>(string path, JsonTypeInfo<T> type, Func<T, ApiResult<T>?>? reject)
        where T : class
    {
        try
        {
            using HttpResponseMessage response = await http.GetAsync(path, _lifetime.Token);
            int status = (int)response.StatusCode;
            if (status == (int)HttpStatusCode.NotFound)
            {
                // The names carry a content hash, so a missing file is one the redeployed site no longer has.
                return SiteUpdated<T>();
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ApiResult<T>(null, status, null);
            }

            T? value = Deserialize(await response.Content.ReadAsStringAsync(_lifetime.Token), type);
            if (value is not null && reject?.Invoke(value) is { } rejected)
            {
                return rejected;
            }

            return new ApiResult<T>(value, status, null);
        }
        catch (HttpRequestException)
        {
            return NoResponse<T>();
        }
        catch (ObjectDisposedException)
        {
            // Asked after the page ended.
            return NoResponse<T>();
        }
        catch (OperationCanceledException)
        {
            // The page ended, or HttpClient's own timeout fired.
            return NoResponse<T>();
        }
    }

    private static T? Deserialize<T>(string body, JsonTypeInfo<T> type)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(body, type);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ApiResult<T> SiteUpdated<T>() => new(default, 404, ApiErrorCodes.SiteUpdated);

    private static ApiResult<T> NoResponse<T>() => new(default, 0, null);
}
