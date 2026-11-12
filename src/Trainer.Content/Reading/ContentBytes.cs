namespace Trainer.Content.Reading;

/// <summary>Byte-level normalisation shared by hashing and parsing so both see the same text.</summary>
internal static class ContentBytes
{
    private static ReadOnlySpan<byte> Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>Strips a leading UTF-8 BOM and converts CRLF to LF.</summary>
    internal static byte[] Normalize(byte[] bytes)
    {
        ReadOnlySpan<byte> source = bytes;
        if (source.StartsWith(Bom))
        {
            source = source[Bom.Length..];
        }

        byte[] result = new byte[source.Length];
        int length = 0;
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == (byte)'\r' && i + 1 < source.Length && source[i + 1] == (byte)'\n')
            {
                continue;
            }

            result[length++] = source[i];
        }

        return result.AsSpan(0, length).ToArray();
    }
}
