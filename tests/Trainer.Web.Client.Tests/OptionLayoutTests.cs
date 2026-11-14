using Trainer.Contracts;
using Trainer.Web.Client.Sessions;

namespace Trainer.Web.Client.Tests;

public sealed class OptionLayoutTests
{
    private static OptionDto[] Options(params string[] texts) =>
        [.. texts.Select((t, i) => new OptionDto("o" + i, t, i == 0, null))];

    [Theory]
    [InlineData(new[] { "<p>012</p>", "<p>333</p>", "<p>222</p>", "<p>Compile error</p>" }, 4)]
    [InlineData(new[] { "<p>012</p>", "<p>333</p>", "<p>222</p>" }, 3)]
    [InlineData(new[] { "<p>true</p>", "<p>false</p>" }, 2)]
    [InlineData(new[] { "<p>42</p>" }, 1)]
    // Markup and entities do not count towards the length a reader sees.
    [InlineData(new[] { "<p><code>List&lt;int&gt;</code></p>", "<p><code>int[]</code></p>", "<p>a</p>", "<p>b</p>", "<p>c</p>" }, 4)]
    public void ShortOptions_TakeUpToFourColumns(string[] texts, int columns) =>
        Assert.Equal(columns, OptionLayout.Columns(Options(texts)));

    [Theory]
    [InlineData(new[] { "<p>The UI hangs forever because of a deadlock</p>", "<p>a</p>", "<p>b</p>", "<p>c</p>" }, 2)]
    [InlineData(new[] { "<p>The continuation runs on a thread pool thread and throws</p>" }, 1)]
    // A block (code, a list) never fits a narrow column, however short.
    [InlineData(new[] { "<pre><code>x</code></pre>", "<p>a</p>", "<p>b</p>" }, 2)]
    [InlineData(new[] { "<p>a</p><p>b</p>", "<p>c</p>", "<p>d</p>" }, 2)]
    public void LongOrBlockOptions_TakeAtMostTwo(string[] texts, int columns) =>
        Assert.Equal(columns, OptionLayout.Columns(Options(texts)));

    [Fact]
    public void NoOptions_IsOneColumn() => Assert.Equal(1, OptionLayout.Columns([]));
}
