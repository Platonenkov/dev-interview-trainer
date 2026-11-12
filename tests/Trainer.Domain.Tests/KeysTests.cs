using Trainer.Domain.Keys;
using Trainer.Domain.Questions;

namespace Trainer.Domain.Tests;

public sealed class KeysTests
{
    [Theory]
    [InlineData("01JD4X7Q8N9V2K3M4P5R6S7T8W", true)]
    [InlineData("01JD4X7Q8N9V2K3M4P5R6S7T8I", false)]
    [InlineData("81JD4X7Q8N9V2K3M4P5R6S7T8W", false)]
    [InlineData("short", false)]
    [InlineData("01JD4X7Q8N9V2K3M4P5R6S7T8W\n", false)]
    [InlineData("01jd4x7q8n9v2k3m4p5r6s7t8w", false)]
    [InlineData(null, false)]
    public void QuestionId_Validates(string? value, bool ok) => Assert.Equal(ok, QuestionId.TryCreate(value, out _));

    [Fact]
    public void QuestionId_KeepsValue()
    {
        Assert.True(QuestionId.TryCreate("01JD4X7Q8N9V2K3M4P5R6S7T8W", out QuestionId id));
        Assert.Equal("01JD4X7Q8N9V2K3M4P5R6S7T8W", id.Value);
    }

    [Theory]
    [InlineData("result-deadlock-ui-thread", true)]
    [InlineData("a", true)]
    [InlineData("a1-b2", true)]
    [InlineData("Result", false)]
    [InlineData("a--b", false)]
    [InlineData("-a", false)]
    [InlineData("a-", false)]
    [InlineData("a\n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Slug_Validates(string? value, bool ok) => Assert.Equal(ok, Slug.TryCreate(value, out _));

    [Fact]
    public void Slug_EnforcesMaxLength()
    {
        Assert.True(Slug.TryCreate(new string('a', 80), out _));
        Assert.False(Slug.TryCreate(new string('a', 81), out _));
    }

    [Fact]
    public void StackKey_And_TopicKey_UseSlugRules()
    {
        Assert.True(StackKey.TryCreate("dotnet", out StackKey stack));
        Assert.Equal("dotnet", stack.Value);
        Assert.False(StackKey.TryCreate("Dot Net", out _));
        Assert.True(TopicKey.TryCreate("async-await", out TopicKey topic));
        Assert.Equal("async-await", topic.Value);
        Assert.False(TopicKey.TryCreate("async--await", out _));
        Assert.False(TopicKey.TryCreate(new string('a', 81), out _));
    }

    [Fact]
    public void Culture_RejectsUnsupported() => Assert.False(CultureKey.TryCreate("de", out _));

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Culture_AcceptsSupported(string value)
    {
        Assert.True(CultureKey.TryCreate(value, out CultureKey culture));
        Assert.Equal(value, culture.Value);
    }

    [Theory]
    [InlineData("RU")]
    [InlineData("en-US")]
    [InlineData("")]
    [InlineData(null)]
    public void Culture_IsCaseSensitiveAndExact(string? value) => Assert.False(CultureKey.TryCreate(value, out _));

    [Fact]
    public void Culture_DefaultIsEnglishAndSupportedListsBoth()
    {
        Assert.Equal("en", CultureKey.Default.Value);
        Assert.Equal(["ru", "en"], CultureKey.Supported.Select(c => c.Value).ToArray());
    }
}
