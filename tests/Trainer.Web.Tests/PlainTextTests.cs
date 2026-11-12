using Trainer.Web.Seo;

namespace Trainer.Web.Tests;

public sealed class PlainTextTests
{
    [Theory]
    [InlineData("<p>a</p><p>b</p>", "a b")]
    [InlineData("a<br>b", "a b")]
    [InlineData("a<br/>b", "a b")]
    [InlineData("<ul><li>one</li><li>two</li></ul>", "one two")]
    [InlineData("<h2>Title</h2><p>Body</p>", "Title Body")]
    public void BlockTagsAndBreaksBecomeASpace(string html, string expected) =>
        Assert.Equal(expected, PlainText.Snippet(html));

    [Theory]
    [InlineData("foo<code>bar</code>baz", "foobarbaz")]
    [InlineData("<p>use <strong>Config</strong>Await</p>", "use ConfigAwait")]
    [InlineData("a<em>b</em>c<a href=\"https://x.test\">d</a>", "abcd")]
    public void InlineTagsVanishWithoutASpace(string html, string expected) =>
        Assert.Equal(expected, PlainText.Snippet(html));

    [Fact]
    public void DecodesEntitiesAndCollapsesWhitespace() =>
        Assert.Equal("a < b & c", PlainText.Snippet("<p>a &lt;\n\n  b   &amp;\tc</p>"));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveMaxLength_IsRejected(int maxLength) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PlainText.Snippet("text", maxLength));

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, PlainText.Snippet(string.Empty));
        Assert.Equal(string.Empty, PlainText.Snippet("<p> </p>"));
    }

    [Fact]
    public void ExactLengthBoundary_IsNotTruncated()
    {
        string exact = new('a', 20);
        Assert.Equal(exact, PlainText.Snippet(exact, 20));

        string truncated = PlainText.Snippet(exact + "b", 20);
        Assert.EndsWith("…", truncated, StringComparison.Ordinal);
        Assert.True(truncated.Length <= 20);
    }

    [Fact]
    public void Truncation_PrefersWordBoundary() =>
        Assert.Equal("alpha beta…", PlainText.Snippet("alpha beta gamma delta", 14));

    [Fact]
    public void Truncation_NeverSplitsASurrogatePair()
    {
        string emoji = char.ConvertFromUtf32(0x1F600);
        // The hard cut at index 9 falls between the two halves of the first emoji.
        string input = new string('a', 8) + emoji + emoji;
        string result = PlainText.Snippet(input, 10);

        Assert.Equal(new string('a', 8) + "…", result);
        AssertNoLoneSurrogates(result);
    }

    [Fact]
    public void Truncation_NeverSplitsATextElement()
    {
        // "e" followed by a combining acute accent is one user-perceived character.
        string input = new string('a', 9) + "é" + "bbbb";
        string result = PlainText.Snippet(input, 11);

        Assert.Equal(new string('a', 9) + "…", result);
        AssertNoLoneSurrogates(result);
    }

    private static void AssertNoLoneSurrogates(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                Assert.True(i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]));
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(value[i]));
            }
        }
    }
}
