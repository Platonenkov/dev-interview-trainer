using Trainer.Content.Reading;

namespace Trainer.Content.Tests;

public sealed class ContentHasherTests
{
    [Fact]
    public void ContentHash_IsIndependentOfLineEndingsAndBom()
    {
        byte[] lf = "a: 1\nb: 2\n"u8.ToArray();
        byte[] crlfBom = [0xEF, 0xBB, 0xBF, .. "a: 1\r\nb: 2\r\n"u8];

        Assert.Equal(ContentHasher.Compute([("x.yaml", lf)]), ContentHasher.Compute([("x.yaml", crlfBom)]));
    }

    [Fact]
    public void ContentHash_ChangesWithPath()
    {
        byte[] bytes = "a: 1\n"u8.ToArray();

        Assert.NotEqual(ContentHasher.Compute([("x.yaml", bytes)]), ContentHasher.Compute([("y.yaml", bytes)]));
    }

    [Fact]
    public void ContentHash_ChangesWithContent()
    {
        Assert.NotEqual(
            ContentHasher.Compute([("x.yaml", "a: 1\n"u8.ToArray())]),
            ContentHasher.Compute([("x.yaml", "a: 2\n"u8.ToArray())]));
    }

    [Fact]
    public void ContentHash_IsIndependentOfInputOrder()
    {
        (string, byte[]) first = ("a.yaml", "1"u8.ToArray());
        (string, byte[]) second = ("b.yaml", "2"u8.ToArray());

        Assert.Equal(ContentHasher.Compute([first, second]), ContentHasher.Compute([second, first]));
    }

    [Fact]
    public void ContentHash_FileBoundaryShift_ChangesTheHash()
    {
        // The concatenation of path, separator and content is identical for both sets ("a\nx\nb\ny"); only the
        // boundary between the files differs, which the old newline-separated scheme could not tell apart.
        string split = ContentHasher.Compute([("a", "x\n"u8.ToArray()), ("b", "y"u8.ToArray())]);
        string merged = ContentHasher.Compute([("a", "x\nb\ny"u8.ToArray())]);

        Assert.NotEqual(split, merged);
    }

    [Fact]
    public void ContentHash_PathContentBoundaryShift_ChangesTheHash()
    {
        string first = ContentHasher.Compute([("x.yaml", "\nabc"u8.ToArray())]);
        string second = ContentHasher.Compute([("x.yaml\nabc", Array.Empty<byte>())]);
        string third = ContentHasher.Compute([("x.yam", "l\nabc"u8.ToArray())]);

        Assert.NotEqual(first, second);
        Assert.NotEqual(first, third);
    }

    [Fact]
    public void ContentHash_EmptyFileIsNotTheSameAsNoFile()
    {
        Assert.NotEqual(ContentHasher.Compute([]), ContentHasher.Compute([("x.yaml", Array.Empty<byte>())]));
    }

    [Fact]
    public void ContentHash_IsLowercaseHexSha256()
    {
        string hash = ContentHasher.Compute([("x.yaml", "a"u8.ToArray())]);

        Assert.Equal(64, hash.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);
    }
}
