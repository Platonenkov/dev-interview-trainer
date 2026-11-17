using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;
using Trainer.Content.Model;
using Trainer.Content.Reading;
using Trainer.TestSupport;
using Trainer.Web.OpenGraph;

namespace Trainer.Web.Tests;

public sealed partial class OgCardRendererTests
{
    private static readonly OgCardRenderer Renderer = new();

    [GeneratedRegex(@"--(?<name>[a-z-]+):\s*(?<hex>#[0-9a-fA-F]{6});", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    private static OgCard Card(string culture) => new(
        culture,
        culture == "ru" ? "Почему Task.Result на UI-потоке приводит к взаимной блокировке?" : "Why does Task.Result on the UI thread deadlock?",
        "middle",
        "async / await",
        "Dev Interview Trainer · trainer.test");

    [Fact]
    public void Render_Is1200x630Png()
    {
        byte[] png = Renderer.RenderPng(Card("en"));

        using SKBitmap bitmap = SKBitmap.Decode(png);
        Assert.Equal((1200, 630), (bitmap.Width, bitmap.Height));
    }

    [Fact]
    public void Render_IsDeterministic() => Assert.Equal(Renderer.RenderPng(Card("ru")), Renderer.RenderPng(Card("ru")));

    [Fact]
    public void Render_RuAndEnDiffer() => Assert.NotEqual(Renderer.RenderPng(Card("ru")), Renderer.RenderPng(Card("en")));

    [Fact]
    public void Render_SiteCardWithoutTopicAndLevel_Renders()
    {
        OgCard site = new("en", "Interview questions for .NET developers.", null, null, "Dev Interview Trainer · trainer.test");

        using SKBitmap bitmap = SKBitmap.Decode(Renderer.RenderPng(site));

        Assert.Equal((1200, 630), (bitmap.Width, bitmap.Height));
    }

    [Fact]
    public void Render_400CharacterTitleWithoutSpaces_DoesNotThrowAndStaysInFourLines()
    {
        string title = new('W', 400);
        OgCard card = new("en", title, "senior", new string('x', 300), new string('y', 300));

        byte[] png = Renderer.RenderPng(card);
        IReadOnlyList<string> lines = OgCardRenderer.WrapTitle(title);

        using SKBitmap bitmap = SKBitmap.Decode(png);
        Assert.Equal((1200, 630), (bitmap.Width, bitmap.Height));
        Assert.Equal(OgCardRenderer.MaxTitleLines, lines.Count);
        Assert.EndsWith("…", lines[^1], StringComparison.Ordinal);
        using SKFont font = new(OgCardRenderer.TitleTypeface, OgCardRenderer.TitleSize);
        Assert.All(lines, line => Assert.True(font.MeasureText(line) <= OgCardRenderer.TextWidth, $"'{line}' is wider than the text column."));
    }

    [Fact]
    public void Wrap_ShortTitle_StaysOnOneLineWithoutEllipsis() =>
        Assert.Equal(["What is a closure?"], OgCardRenderer.WrapTitle("What is a closure?"));

    [Fact]
    public void Wrap_LongWordsAndSpaces_BreaksOnSpacesFirst()
    {
        IReadOnlyList<string> lines = OgCardRenderer.WrapTitle(string.Join(' ', Enumerable.Repeat("synchronization", 12)));

        Assert.InRange(lines.Count, 2, OgCardRenderer.MaxTitleLines);
        Assert.All(lines.Take(lines.Count - 1), line => Assert.All(line.Split(' '), word => Assert.Equal("synchronization", word)));
    }

    [Fact]
    public void Wrap_SurrogatePairsAreNeverSplit()
    {
        string title = string.Concat(Enumerable.Repeat("\U0001F600", 200));

        IReadOnlyList<string> lines = OgCardRenderer.WrapTitle(title);

        Assert.All(lines, line => Assert.True(IsWellFormed(line), "A line holds half of a surrogate pair."));

        static bool IsWellFormed(string text)
        {
            try
            {
                _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetBytes(text);
                return true;
            }
            catch (EncoderFallbackException)
            {
                return false;
            }
        }
    }

    [Fact]
    public async Task Fonts_CoverEveryCharacterOfEveryRepositoryTitle()
    {
        ContentSet set = await new ContentReader().ReadAsync(
            Path.Combine(ContentWorkspace.RepositoryRoot(), "content"), TestContext.Current.CancellationToken);

        List<string> titles = [.. set.Questions.SelectMany(q => q.Document.Locales.Values.Select(l => l.Title))];
        List<string> topicTitles =
        [
            .. set.TopicsByStack.Values.SelectMany(topics => topics.SelectMany(t => t.Locales.Values.Select(l => l.Title))),
            .. set.Stacks.SelectMany(s => s.Locales.Values.Select(l => l.Title)),
        ];

        Assert.NotEmpty(titles);
        // The footer and the card text also carry these two characters.
        Assert.Empty(MissingGlyphs(OgCardRenderer.TitleTypeface, [.. titles, "…·"]));
        Assert.Empty(MissingGlyphs(OgCardRenderer.MonoTypeface, [.. topicTitles, "~/…"]));
    }

    private static List<string> MissingGlyphs(SKTypeface typeface, IEnumerable<string> texts)
    {
        HashSet<int> missing = [];
        foreach (string text in texts)
        {
            foreach (Rune rune in text.EnumerateRunes())
            {
                if (!Rune.IsWhiteSpace(rune) && !Rune.IsControl(rune) && !typeface.ContainsGlyph(rune.Value))
                {
                    missing.Add(rune.Value);
                }
            }
        }

        return [.. missing.Order().Select(code => $"U+{code:X4}")];
    }

    [Fact]
    public void Palette_MatchesDarkThemeTokens()
    {
        string css = File.ReadAllText(Path.Combine(ContentWorkspace.RepositoryRoot(), "src", "Trainer.Web", "wwwroot", "css", "tokens.css"));
        int start = css.IndexOf("[data-theme=\"dark\"]", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int open = css.IndexOf('{', start);
        int close = css.IndexOf('}', open);
        Dictionary<string, string> tokens = Token().Matches(css[open..close])
            .ToDictionary(m => m.Groups["name"].Value, m => m.Groups["hex"].Value, StringComparer.Ordinal);

        Assert.Equal(OgPalette.Background, tokens["bg"], ignoreCase: true);
        Assert.Equal(OgPalette.Panel, tokens["panel"], ignoreCase: true);
        Assert.Equal(OgPalette.Border, tokens["border"], ignoreCase: true);
        Assert.Equal(OgPalette.Text, tokens["text"], ignoreCase: true);
        Assert.Equal(OgPalette.Strong, tokens["strong"], ignoreCase: true);
        Assert.Equal(OgPalette.Muted, tokens["muted"], ignoreCase: true);
        Assert.Equal(OgPalette.Accent, tokens["accent"], ignoreCase: true);
        Assert.Equal(OgPalette.LevelColor("junior"), tokens["lv-junior"], ignoreCase: true);
        Assert.Equal(OgPalette.LevelColor("middle"), tokens["lv-middle"], ignoreCase: true);
        Assert.Equal(OgPalette.LevelColor("senior"), tokens["lv-senior"], ignoreCase: true);
        Assert.Equal(OgPalette.LevelColor("lead"), tokens["lv-lead"], ignoreCase: true);
        Assert.Equal(OgPalette.LevelColor("architect"), tokens["lv-architect"], ignoreCase: true);
    }

    [Fact]
    public void Fingerprint_IsSixteenHexCharactersAndCoversEveryField()
    {
        OgCard card = Card("en");
        string fingerprint = OgFingerprint.Of(card);

        Assert.Matches("^[0-9a-f]{16}$", fingerprint);
        Assert.Equal(fingerprint, OgFingerprint.Of(card with { }));
        Assert.NotEqual(fingerprint, OgFingerprint.Of(card with { Culture = "ru" }));
        Assert.NotEqual(fingerprint, OgFingerprint.Of(card with { Title = card.Title + "?" }));
        Assert.NotEqual(fingerprint, OgFingerprint.Of(card with { LevelKey = "senior" }));
        Assert.NotEqual(fingerprint, OgFingerprint.Of(card with { LevelKey = null }));
        Assert.NotEqual(fingerprint, OgFingerprint.Of(card with { TopicTitle = "LINQ" }));
        Assert.NotEqual(fingerprint, OgFingerprint.Of(card with { Footer = "other" }));
    }

    [Fact]
    public async Task Cache_UnwritableDirectory_ServesTheImageAndKeepsWorking()
    {
        string blocker = Path.Combine(Path.GetTempPath(), "dit-og-blocker-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(blocker, "a file where a directory is needed", TestContext.Current.CancellationToken);
        try
        {
            CapturingLoggerProvider logs = new();
            using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
            using OgImageCache cache = new(
                Options.Create(new OpenGraphOptions { CacheDirectory = Path.Combine(blocker, "og") }),
                factory.CreateLogger<OgImageCache>());
            byte[] expected = [1, 2, 3];

            byte[] first = await cache.GetOrRenderAsync("en-site-0.png", () => expected, TestContext.Current.CancellationToken);
            byte[] second = await cache.GetOrRenderAsync("en-site-0.png", () => expected, TestContext.Current.CancellationToken);
            await cache.InvalidateAsync(TestContext.Current.CancellationToken);

            Assert.Equal(expected, first);
            Assert.Equal(expected, second);
            CapturedLogEntry warning = Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning);
            Assert.Contains("could not be written", warning.Message, StringComparison.Ordinal);
            // The real exception type is logged, so a file lock does not read as a permission problem.
            Assert.Matches("^(IOException|DirectoryNotFoundException|UnauthorizedAccessException)$", warning.State["ExceptionType"]?.ToString());
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public async Task Cache_StoresOnceAndInvalidateEmptiesTheDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dit-og-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using OgImageCache cache = new(
                Options.Create(new OpenGraphOptions { CacheDirectory = directory }),
                NullLogger<OgImageCache>.Instance);
            int renders = 0;

            byte[] first = await cache.GetOrRenderAsync("en-site-1.png", () => { renders++; return [9]; }, TestContext.Current.CancellationToken);
            byte[] second = await cache.GetOrRenderAsync("en-site-1.png", () => { renders++; return [8]; }, TestContext.Current.CancellationToken);

            Assert.Equal([9], first);
            Assert.Equal([9], second);
            Assert.Equal(1, renders);
            Assert.Equal(["en-site-1.png"], Directory.EnumerateFiles(directory).Select(Path.GetFileName));

            await cache.InvalidateAsync(TestContext.Current.CancellationToken);

            Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Cache_Invalidate_KeepsAFreshTempFileAndSweepsAStaleOne()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dit-og-tmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string fresh = Path.Combine(directory, "en.site.aaaa.png.0123.tmp");
            string stale = Path.Combine(directory, "en.site.bbbb.png.4567.tmp");
            string card = Path.Combine(directory, "en.site.cccc.png");
            await File.WriteAllBytesAsync(fresh, [1], TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(stale, [1], TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(card, [1], TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-1));
            using OgImageCache cache = new(
                Options.Create(new OpenGraphOptions { CacheDirectory = directory }), NullLogger<OgImageCache>.Instance);

            await cache.InvalidateAsync(TestContext.Current.CancellationToken);

            Assert.Equal([fresh], Directory.EnumerateFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Cache_TempFileRemovedBeforeTheMove_NeverLogsAnUnwritableWarning()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows reports a temp file with a pending delete as access denied, so the lost race is not reproducible there.");
        string directory = Path.Combine(Path.GetTempPath(), "dit-og-race-" + Guid.NewGuid().ToString("N"));
        try
        {
            CapturingLoggerProvider logs = new();
            using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
            using OgImageCache cache = new(
                Options.Create(new OpenGraphOptions { CacheDirectory = directory }), factory.CreateLogger<OgImageCache>(), TimeSpan.FromMinutes(1));
            byte[] png = new byte[256 * 1024];
            using CancellationTokenSource stop = new();

            // Removes temp files outright, as the old invalidation did, to force the lost race between a write and its
            // move. Deleting cached cards is left out: Windows reports a card with a pending delete as access denied.
            Task sweeping = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    foreach (string temp in Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.tmp") : [])
                    {
                        try
                        {
                            File.Delete(temp);
                        }
                        catch (IOException)
                        {
                        }
                    }
                }
            }, CancellationToken.None);

            byte[][] results = await Task.WhenAll(Enumerable.Range(0, 150).Select(i =>
                cache.GetOrRenderAsync($"en.site.{i}.png", () => png, TestContext.Current.CancellationToken)));
            await stop.CancelAsync();
            await sweeping;

            Assert.All(results, r => Assert.Equal(png.Length, r.Length));
            Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Warning);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Cache_FileNames_NeverCollideForDistinctStackAndSlugPairs()
    {
        (string Stack, string Slug)[] pairs = [("a-b", "c"), ("a", "b-c"), ("a", "b"), ("a-b-c", "d"), ("a-b", "c-d"), ("a", "b-c-d")];

        string[] names = [.. pairs.Select(p => OgImageCache.QuestionFileName("en", p.Stack, p.Slug, "0123456789abcdef"))];

        Assert.Equal(pairs.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.NotEqual(OgImageCache.SiteFileName("en", "0123456789abcdef"), OgImageCache.QuestionFileName("en", "site", "x", "0123456789abcdef"));
    }

    [Fact]
    public async Task Cache_AllRenderSlotsBusy_ThrowsAfterTheBoundedWait()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dit-og-busy-" + Guid.NewGuid().ToString("N"));
        try
        {
            using OgImageCache cache = new(
                Options.Create(new OpenGraphOptions { CacheDirectory = directory }), NullLogger<OgImageCache>.Instance, TimeSpan.FromMilliseconds(50));
            using ManualResetEventSlim release = new();
            List<Task<byte[]>> holders = [];
            for (int i = 0; i < OgImageCache.MaxConcurrentRenders; i++)
            {
                holders.Add(cache.GetOrRenderAsync($"en.site.{i}.png", () => { release.Wait(); return [1]; }, TestContext.Current.CancellationToken));
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<OgRenderBusyException>(
                () => cache.GetOrRenderAsync("en.site.busy.png", () => [2], TestContext.Current.CancellationToken));

            release.Set();
            await Task.WhenAll(holders);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Cache_MissingDirectory_InvalidateIsANoOp()
    {
        using OgImageCache cache = new(
            Options.Create(new OpenGraphOptions { CacheDirectory = Path.Combine(Path.GetTempPath(), "dit-og-absent-" + Guid.NewGuid().ToString("N")) }),
            NullLogger<OgImageCache>.Instance);

        await cache.InvalidateAsync(TestContext.Current.CancellationToken);
    }
}
