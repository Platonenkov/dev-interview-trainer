using System.Security.Cryptography;

namespace Trainer.Domain.Ids;

/// <summary>
/// Creates ULIDs from the injected clock and a cryptographic random source. Used by the server (user ids) and by
/// the WebAssembly client (answer and mock-session ids). Ids are not monotonic within one millisecond.
/// </summary>
public sealed class UlidGenerator(TimeProvider time)
{
    public string Next()
    {
        Span<byte> randomness = stackalloc byte[10];
        RandomNumberGenerator.Fill(randomness);
        return UlidText.Create(time.GetUtcNow(), randomness);
    }
}
