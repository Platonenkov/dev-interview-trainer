using System.Security.Cryptography;

namespace Trainer.Web.StaticSite;

/// <summary>The content hash that names a content shard (ADR 0023).</summary>
public static class StaticContentHash
{
    /// <summary>Length of the hash in a file name, in hex characters.</summary>
    public const int Length = 16;

    /// <summary>The first <see cref="Length"/> lowercase hex characters of the SHA-256 of <paramref name="bytes"/>.</summary>
    public static string Of(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes))[..Length];

    /// <summary>True for a string a <see cref="Of"/> call can produce; anything else cannot name a shard.</summary>
    public static bool IsValid(string? hash)
    {
        if (hash is null || hash.Length != Length)
        {
            return false;
        }

        foreach (char c in hash)
        {
            if (!char.IsAsciiDigit(c) && !(c is >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
