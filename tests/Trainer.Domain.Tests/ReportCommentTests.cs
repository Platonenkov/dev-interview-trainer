using Trainer.Domain.Feedback;

namespace Trainer.Domain.Tests;

public sealed class ReportCommentTests
{
    [Fact]
    public void Comment_StripsControlCharsKeepsNewlines()
    {
        Assert.True(ReportComment.TryNormalise(" a\r\nb\u0007 ", out string? comment));
        Assert.Equal("a\nb", comment);
    }

    [Fact]
    public void Comment_LoneCarriageReturnBecomesNewline()
    {
        Assert.True(ReportComment.TryNormalise("a\rb", out string? comment));
        Assert.Equal("a\nb", comment);
    }

    [Fact]
    public void Comment_KeepsTabsAndUnicode()
    {
        Assert.True(ReportComment.TryNormalise("a\tb \u043f\u0440\u0438\u0432\u0435\u0442", out string? comment));
        Assert.Equal("a\tb \u043f\u0440\u0438\u0432\u0435\u0442", comment);
    }

    [Fact]
    public void Comment_StripsBidiOverridesIsolatesAndZeroWidthCharacters()
    {
        Assert.True(ReportComment.TryNormalise("a\u202Eb\u200Bc\u2066d\u2069e\uFEFFf\u200Dg", out string? comment));
        Assert.Equal("abcdefg", comment);
    }

    [Fact]
    public void Comment_TooLong_Rejected() => Assert.False(ReportComment.TryNormalise(new string('x', ReportComment.MaxLength + 1), out _));

    [Fact]
    public void Comment_ExactlyMaxLength_Accepted()
    {
        Assert.True(ReportComment.TryNormalise(new string('x', ReportComment.MaxLength), out string? comment));
        Assert.Equal(ReportComment.MaxLength, comment!.Length);
    }

    [Fact]
    public void Comment_LengthIsMeasuredAfterNormalising()
    {
        // 1000 characters plus padding and control characters that normalising removes.
        string raw = "  \u0007" + new string('x', ReportComment.MaxLength) + "\u0007  ";

        Assert.True(ReportComment.TryNormalise(raw, out string? comment));
        Assert.Equal(ReportComment.MaxLength, comment!.Length);
    }

    [Fact]
    public void Comment_CrlfCountsAsOneCharacter()
    {
        string raw = string.Concat(Enumerable.Repeat("\r\n", ReportComment.MaxLength - 2));

        Assert.True(ReportComment.TryNormalise("x" + raw + "x", out string? comment));
        Assert.Equal(ReportComment.MaxLength, comment!.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    [InlineData("\u0007")]
    public void Comment_Blank_IsNull(string? raw)
    {
        Assert.True(ReportComment.TryNormalise(raw, out string? comment));
        Assert.Null(comment);
    }
}
