using SkiaSharp;

namespace Trainer.Web.OpenGraph;

/// <summary>
/// The <c>[data-theme="dark"]</c> colours of <c>wwwroot/css/tokens.css</c>. A test parses the stylesheet and compares, so
/// a changed token fails the build instead of silently leaving the cards in the old colours.
/// </summary>
public static class OgPalette
{
    public const string Background = "#0d1117";
    public const string Panel = "#161b22";
    public const string Border = "#30363d";
    public const string Text = "#c9d1d9";
    public const string Strong = "#e6edf3";
    public const string Muted = "#8b949e";
    public const string Accent = "#f78166";

    public const string LevelJunior = "#3fb950";
    public const string LevelMiddle = "#d29922";
    public const string LevelSenior = "#f78166";
    public const string LevelLead = "#d2a8ff";
    public const string LevelArchitect = "#58a6ff";

    public static string LevelColor(string? levelKey) => levelKey switch
    {
        "junior" => LevelJunior,
        "middle" => LevelMiddle,
        "senior" => LevelSenior,
        "lead" => LevelLead,
        "architect" => LevelArchitect,
        _ => Muted,
    };

    internal static SKColor Parse(string hex) => SKColor.Parse(hex);
}
