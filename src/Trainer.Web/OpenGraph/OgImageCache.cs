using Microsoft.Extensions.Options;
using Trainer.Application.Abstractions;

namespace Trainer.Web.OpenGraph;

/// <summary>
/// Disk cache of rendered cards, one file per <c>culture.stack.slug.fingerprint</c>. A render is CPU work that anyone
/// can trigger by requesting a valid URL, so at most <see cref="MaxConcurrentRenders"/> run at a time. The cache is an
/// optimisation only: when the directory is missing or unwritable the image is still served, just not stored.
/// </summary>
public sealed partial class OgImageCache : IContentCacheInvalidator, IDisposable
{
    public const int MaxConcurrentRenders = 2;

    /// <summary>How long a request waits for a render slot before it gives up (see <see cref="OgRenderBusyException"/>).</summary>
    public static readonly TimeSpan DefaultRenderWait = TimeSpan.FromSeconds(2);

    private const string Extension = ".png";
    private const char Separator = '.';
    private const string TempExtension = ".tmp";

    // A temp file lives for one write and one move. One that is older than this belongs to a crashed process.
    private static readonly TimeSpan StaleTempAge = TimeSpan.FromMinutes(10);

    private readonly string _directory;
    private readonly ILogger<OgImageCache> _logger;
    private readonly TimeSpan _renderWait;
    private readonly SemaphoreSlim _renders = new(MaxConcurrentRenders, MaxConcurrentRenders);
    private int _storeFailureLogged;

    public OgImageCache(IOptions<OpenGraphOptions> options, ILogger<OgImageCache> logger, TimeSpan? renderWait = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        string? configured = options.Value.CacheDirectory;
        _directory = string.IsNullOrWhiteSpace(configured) ? Path.Combine(Path.GetTempPath(), "dit-og") : configured;
        _logger = logger;
        _renderWait = renderWait ?? DefaultRenderWait;
    }

    /// <summary>
    /// The file name of a question card. Parts are joined with a dot, which a stack key or slug (lowercase kebab-case)
    /// never contains, so two different stack and slug pairs cannot share a name.
    /// </summary>
    public static string QuestionFileName(string culture, string stack, string slug, string fingerprint) =>
        $"{culture}{Separator}{stack}{Separator}{slug}{Separator}{fingerprint}{Extension}";

    public static string SiteFileName(string culture, string fingerprint) =>
        $"{culture}{Separator}site{Separator}{fingerprint}{Extension}";

    /// <summary>The cached PNG, or the result of <paramref name="render"/> after storing it.</summary>
    public async Task<byte[]> GetOrRenderAsync(string fileName, Func<byte[]> render, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentNullException.ThrowIfNull(render);
        string path = Path.Combine(_directory, fileName);

        if (await TryReadAsync(path, ct) is { } cached)
        {
            return cached;
        }

        // Bounded: with an unwritable volume every request renders, and an unbounded queue would pile up behind two slots.
        if (!await _renders.WaitAsync(_renderWait, ct))
        {
            throw new OgRenderBusyException();
        }

        try
        {
            // Another request may have stored it while this one waited for a slot.
            if (await TryReadAsync(path, ct) is { } stored)
            {
                return stored;
            }

            byte[] png = await Task.Run(render, ct);
            await TryStoreAsync(path, png, ct);
            return png;
        }
        finally
        {
            _renders.Release();
        }
    }

    /// <summary>
    /// Deletes every cached card. Cards of unchanged questions are rendered again on demand, which is cheaper than
    /// tracking which imports changed which card. A temp file is left alone unless it is stale: a fresh one is a render
    /// in progress, and deleting it would fail that render's move. A missing directory is an empty cache.
    /// </summary>
    public Task InvalidateAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_directory))
        {
            return Task.CompletedTask;
        }

        List<Exception> failures = [];
        foreach (string file in Directory.EnumerateFiles(_directory))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (IsFreshTemp(file))
                {
                    continue;
                }

                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add(ex);
            }
        }

        return failures.Count == 0
            ? Task.CompletedTask
            : Task.FromException(new AggregateException("The Open Graph image cache could not be cleared completely.", failures));
    }

    public void Dispose() => _renders.Dispose();

    private static bool IsFreshTemp(string file) =>
        file.EndsWith(TempExtension, StringComparison.Ordinal)
        && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < StaleTempAge;

    private static async Task<byte[]?> TryReadAsync(string path, CancellationToken ct)
    {
        try
        {
            return await File.ReadAllBytesAsync(path, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Includes FileNotFound and DirectoryNotFound: a miss, not a failure.
            return null;
        }
    }

    // Written under a unique temp name and moved, so a reader or a concurrent writer never sees a partial PNG.
    private async Task TryStoreAsync(string path, byte[] png, CancellationToken ct)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + TempExtension;
        bool written = false;
        try
        {
            Directory.CreateDirectory(_directory);
            await File.WriteAllBytesAsync(temp, png, ct);
            written = true;
            File.Move(temp, path, overwrite: true);
            Volatile.Write(ref _storeFailureLogged, 0);
        }
        catch (Exception ex) when (written && ex is FileNotFoundException or UnauthorizedAccessException && !File.Exists(temp))
        {
            // The temp file was written and then removed before the move (Windows reports a file with a pending delete
            // as access denied). That is a lost race, not an unwritable volume, so it neither logs nor arms the
            // once-per-process warning.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            ReportStoreFailure(ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stale temp file is swept by the next invalidation.
        }
    }

    // Once per process: with an unwritable volume every miss fails, and a warning per request would flood the log.
    private void ReportStoreFailure(Exception ex)
    {
        if (Interlocked.Exchange(ref _storeFailureLogged, 1) == 0)
        {
            LogStoreFailed(ex, _directory, ex.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Open Graph image cache in {Directory} could not be written ({ExceptionType}); images are served without caching.")]
    private partial void LogStoreFailed(Exception exception, string directory, string exceptionType);
}
