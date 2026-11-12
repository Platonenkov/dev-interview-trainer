using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Trainer.Content.Reading;

/// <summary>
/// Content fingerprint independent of checkout line endings and editor BOMs. Each file contributes its length-prefixed
/// path and length-prefixed normalised bytes. The render version is not part of it: the importer compares the pair
/// (hash, render version).
/// </summary>
public static class ContentHasher
{
    public static string Compute(IEnumerable<(string RelativePath, byte[] Bytes)> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach ((string path, byte[] bytes) in files.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            AppendFramed(hash, Encoding.UTF8.GetBytes(path));
            AppendFramed(hash, ContentBytes.Normalize(bytes));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// Every field is preceded by its length, so moving bytes across a path/content or file/file boundary always changes
    /// the digest. A bare concatenation, or a separator that file content can contain, would not guarantee that.
    /// </summary>
    private static void AppendFramed(IncrementalHash hash, byte[] data)
    {
        Span<byte> length = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(length, data.Length);
        hash.AppendData(length);
        hash.AppendData(data);
    }
}
