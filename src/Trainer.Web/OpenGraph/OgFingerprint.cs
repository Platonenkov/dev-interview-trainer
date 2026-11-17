using System.Security.Cryptography;
using System.Text;

namespace Trainer.Web.OpenGraph;

/// <summary>
/// The URL token of a card. It covers every field the card shows plus <see cref="Version"/>, so a changed title gets a
/// new URL (crawlers keep the old immutable image for the old URL) and a redesign needs only a version bump.
/// </summary>
public static class OgFingerprint
{
    /// <summary>Bump when <see cref="OgCardRenderer"/> changes how a card looks.</summary>
    public const int Version = 1;

    private const int HexLength = 16;

    public static string Of(OgCard card)
    {
        ArgumentNullException.ThrowIfNull(card);

        // A newline cannot occur inside a field's meaning, so joined fields do not collide the way concatenated ones would.
        string joined = string.Join(
            '\n',
            Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            card.Culture,
            card.Title,
            card.LevelKey ?? string.Empty,
            card.TopicTitle ?? string.Empty,
            card.Footer);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexStringLower(hash)[..HexLength];
    }
}
