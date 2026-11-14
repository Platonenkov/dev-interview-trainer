namespace Trainer.Domain.Ids;

/// <summary>
/// Validation and encoding of ULID text: 26 uppercase Crockford base32 characters. Written without a regex so the
/// WebAssembly client does not pull in the regex engine just for ids.
/// </summary>
public static class UlidText
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int Length = 26;
    private const int RandomnessLength = 10;
    private const long MaxTimestampMilliseconds = (1L << 48) - 1;

    /// <summary>
    /// True for exactly 26 characters whose first one is 0-7 (a ULID is 128 bits, so the leading character of the
    /// 130-bit encoding cannot exceed 7) and the rest are Crockford base32. Lowercase and trailing whitespace are rejected.
    /// </summary>
    public static bool IsValid(string? value)
    {
        if (value is null || value.Length != Length || value[0] is < '0' or > '7')
        {
            return false;
        }

        for (int i = 1; i < Length; i++)
        {
            if (!IsCrockford(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Encodes 48-bit Unix milliseconds followed by exactly 10 random bytes.</summary>
    /// <exception cref="ArgumentException">The time is outside 1970..year 10889 or the randomness is not 10 bytes.</exception>
    public static string Create(DateTimeOffset time, ReadOnlySpan<byte> randomness)
    {
        if (randomness.Length != RandomnessLength)
        {
            throw new ArgumentException("A ULID needs exactly 10 random bytes.", nameof(randomness));
        }

        long milliseconds = time.ToUnixTimeMilliseconds();
        if (milliseconds is < 0 or > MaxTimestampMilliseconds)
        {
            throw new ArgumentException("A ULID time must fit 48 bits of Unix milliseconds.", nameof(time));
        }

        UInt128 value = (UInt128)(ulong)milliseconds << 80;
        for (int i = 0; i < RandomnessLength; i++)
        {
            value |= (UInt128)randomness[i] << (8 * (RandomnessLength - 1 - i));
        }

        Span<char> chars = stackalloc char[Length];
        for (int i = Length - 1; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(value & 31)];
            value >>= 5;
        }

        return new string(chars);
    }

    private static bool IsCrockford(char c) =>
        c is >= '0' and <= '9'
            or >= 'A' and <= 'H'
            or 'J' or 'K' or 'M' or 'N'
            or >= 'P' and <= 'T'
            or >= 'V' and <= 'Z';
}
