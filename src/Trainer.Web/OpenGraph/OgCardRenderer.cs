using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace Trainer.Web.OpenGraph;

/// <summary>
/// Draws the 1200 by 630 Open Graph card as a PNG: dark theme, a monospace <c>~/dotnet/topic</c> header, a level chip,
/// the title in Inter SemiBold wrapped to at most <see cref="MaxTitleLines"/> lines, and the site footer. The result is
/// a pure function of the card, so it can be cached under <see cref="OgFingerprint"/>.
/// </summary>
public sealed partial class OgCardRenderer
{
    public const int Width = 1200;
    public const int Height = 630;
    public const int MaxTitleLines = 4;

    public const float TitleSize = 60;
    public const float TextWidth = Width - 2 * Margin;

    private const float Margin = 80;
    private const float TitleLineHeight = 74;
    private const float FirstTitleBaseline = 300;
    private const float HeaderSize = 30;
    private const float HeaderBaseline = 112;
    private const float ChipSize = 26;
    private const float ChipTop = 144;
    private const float ChipHeight = 48;
    private const float ChipPadding = 20;
    private const float FooterSize = 28;
    private const float FooterBaseline = 594;
    private const float FooterRuleY = 548;
    private const float AccentBarWidth = 12;
    private const string Ellipsis = "…";
    private const string ResourcePrefix = "Trainer.Web.OpenGraph.Fonts.";

    private static readonly Lazy<SKTypeface> TitleFace = new(() => LoadTypeface("Inter-SemiBold.ttf"), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<SKTypeface> MonoFace = new(() => LoadTypeface("JetBrainsMono-Bold.ttf"), LazyThreadSafetyMode.ExecutionAndPublication);

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    /// <summary>The title and footer face; exposed so a test can check that it covers the content's characters.</summary>
    public static SKTypeface TitleTypeface => TitleFace.Value;

    /// <summary>The header and chip face.</summary>
    public static SKTypeface MonoTypeface => MonoFace.Value;

    public byte[] RenderPng(OgCard card)
    {
        ArgumentNullException.ThrowIfNull(card);

        SKImageInfo info = new(Width, Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using SKSurface surface = SKSurface.Create(info);
        SKCanvas canvas = surface.Canvas;
        canvas.Clear(OgPalette.Parse(OgPalette.Background));

        using SKPaint paint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
        paint.Color = OgPalette.Parse(OgPalette.Accent);
        canvas.DrawRect(0, 0, AccentBarWidth, Height, paint);

        DrawHeader(canvas, card, paint);
        DrawTitle(canvas, card, paint);
        DrawFooter(canvas, card, paint);

        using SKImage image = surface.Snapshot();
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void DrawHeader(SKCanvas canvas, OgCard card, SKPaint paint)
    {
        using SKFont mono = CreateFont(MonoTypeface, HeaderSize);
        string path = string.IsNullOrWhiteSpace(card.TopicTitle) ? "~/dotnet" : "~/dotnet/" + Normalise(card.TopicTitle);
        paint.Color = OgPalette.Parse(OgPalette.Muted);
        canvas.DrawText(Fit(path, mono, TextWidth), Margin, HeaderBaseline, mono, paint);

        if (string.IsNullOrEmpty(card.LevelKey))
        {
            return;
        }

        using SKFont chipFont = CreateFont(MonoTypeface, ChipSize);
        SKColor levelColor = OgPalette.Parse(OgPalette.LevelColor(card.LevelKey));
        float chipWidth = chipFont.MeasureText(card.LevelKey) + 2 * ChipPadding;
        SKRect chip = new(Margin, ChipTop, Margin + chipWidth, ChipTop + ChipHeight);

        paint.Color = OgPalette.Parse(OgPalette.Panel);
        canvas.DrawRoundRect(chip, 10, 10, paint);
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 2;
        paint.Color = levelColor;
        canvas.DrawRoundRect(chip, 10, 10, paint);
        paint.Style = SKPaintStyle.Fill;
        canvas.DrawText(card.LevelKey, Margin + ChipPadding, ChipTop + ChipHeight - 13, chipFont, paint);
    }

    private static void DrawTitle(SKCanvas canvas, OgCard card, SKPaint paint)
    {
        using SKFont font = CreateFont(TitleTypeface, TitleSize);
        paint.Color = OgPalette.Parse(OgPalette.Strong);
        List<string> lines = WrapTitle(card.Title, font);
        for (int i = 0; i < lines.Count; i++)
        {
            canvas.DrawText(lines[i], Margin, FirstTitleBaseline + i * TitleLineHeight, font, paint);
        }
    }

    private static void DrawFooter(SKCanvas canvas, OgCard card, SKPaint paint)
    {
        paint.Color = OgPalette.Parse(OgPalette.Border);
        canvas.DrawRect(Margin, FooterRuleY, TextWidth, 2, paint);

        using SKFont font = CreateFont(TitleTypeface, FooterSize);
        paint.Color = OgPalette.Parse(OgPalette.Muted);
        canvas.DrawText(Fit(Normalise(card.Footer), font, TextWidth), Margin, FooterBaseline, font, paint);
    }

    /// <summary>The lines the title is drawn in, at <see cref="TitleSize"/> within <see cref="TextWidth"/>.</summary>
    public static IReadOnlyList<string> WrapTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        using SKFont font = CreateFont(TitleTypeface, TitleSize);
        return WrapTitle(title, font);
    }

    /// <summary>
    /// Greedy wrap on spaces. A word wider than a line is broken between text elements (never inside a surrogate pair or
    /// a combining sequence). Text beyond <see cref="MaxTitleLines"/> lines is cut and the last line ends with an ellipsis.
    /// </summary>
    private static List<string> WrapTitle(string title, SKFont font)
    {
        List<string> lines = [];
        string current = string.Empty;
        foreach (string word in Normalise(title).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = current.Length == 0 ? word : current + " " + word;
            if (font.MeasureText(candidate) <= TextWidth)
            {
                current = candidate;
                continue;
            }

            if (current.Length > 0)
            {
                lines.Add(current);
                current = string.Empty;
            }

            current = font.MeasureText(word) <= TextWidth ? word : BreakByElements(word, font, lines);

            // Everything past the limit is dropped anyway; stopping here keeps a huge title cheap.
            if (lines.Count > MaxTitleLines)
            {
                break;
            }
        }

        if (current.Length > 0)
        {
            lines.Add(current);
        }

        if (lines.Count <= MaxTitleLines)
        {
            return lines;
        }

        lines.RemoveRange(MaxTitleLines, lines.Count - MaxTitleLines);
        lines[MaxTitleLines - 1] = Fit(lines[MaxTitleLines - 1] + Ellipsis, font, TextWidth);
        return lines;
    }

    // Adds every full line of the word to lines and returns the remainder as the new current line.
    private static string BreakByElements(string word, SKFont font, List<string> lines)
    {
        string current = string.Empty;
        foreach (string element in TextElements(word))
        {
            string candidate = current + element;
            if (current.Length == 0 || font.MeasureText(candidate) <= TextWidth)
            {
                current = candidate;
                continue;
            }

            lines.Add(current);
            current = element;
            if (lines.Count > MaxTitleLines)
            {
                break;
            }
        }

        return current;
    }

    /// <summary>
    /// Returns the text unchanged when it fits. Otherwise drops text elements from the end until the rest and an ellipsis
    /// fit; a trailing ellipsis already on the text is replaced, never doubled.
    /// </summary>
    private static string Fit(string text, SKFont font, float maxWidth)
    {
        if (font.MeasureText(text) <= maxWidth)
        {
            return text;
        }

        string body = text.EndsWith(Ellipsis, StringComparison.Ordinal) ? text[..^Ellipsis.Length] : text;
        List<string> elements = [.. TextElements(body)];
        while (elements.Count > 0)
        {
            elements.RemoveAt(elements.Count - 1);
            string candidate = string.Concat(elements).TrimEnd() + Ellipsis;
            if (font.MeasureText(candidate) <= maxWidth)
            {
                return candidate;
            }
        }

        return Ellipsis;
    }

    private static IEnumerable<string> TextElements(string text)
    {
        TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            yield return (string)enumerator.Current;
        }
    }

    private static string Normalise(string? text) => Whitespace().Replace(text ?? string.Empty, " ").Trim();

    // Hinting is off and positions are subpixel so the glyph outlines, and so the PNG bytes, do not depend on a hinter.
    private static SKFont CreateFont(SKTypeface typeface, float size) => new(typeface, size)
    {
        Edging = SKFontEdging.SubpixelAntialias,
        Subpixel = true,
        Hinting = SKFontHinting.None,
    };

    private static SKTypeface LoadTypeface(string fileName)
    {
        Assembly assembly = typeof(OgCardRenderer).Assembly;
        using Stream resource = assembly.GetManifestResourceStream(ResourcePrefix + fileName)
            ?? throw new InvalidOperationException($"The embedded font {fileName} is missing.");
        MemoryStream copy = new();
        resource.CopyTo(copy);
        copy.Position = 0;
        // The typeface keeps reading from the stream, so ownership passes to it.
        return SKTypeface.FromStream(copy) ?? throw new InvalidOperationException($"The embedded font {fileName} is not a valid font.");
    }
}
