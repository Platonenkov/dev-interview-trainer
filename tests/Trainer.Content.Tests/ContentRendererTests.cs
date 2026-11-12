using System.Text.RegularExpressions;
using Trainer.Content.Rendering;

namespace Trainer.Content.Tests;

public sealed partial class ContentRendererTests
{
    private static readonly ContentRenderer R = new();

    [Fact]
    public void RawHtml_IsEscaped() =>
        Assert.DoesNotContain("<script", R.RenderMarkdown("<script>alert(1)</script>"), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void RawHtmlBlock_IsEscaped()
    {
        string html = R.RenderMarkdown("<div onclick=\"x()\">a</div>\n\n<img src=x onerror=alert(1)>");

        Assert.DoesNotContain("<div", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[x](https://a.dev){onmouseover=alert(1)}")]
    [InlineData("![x](https://a.dev/i.png){onerror=alert(1)}")]
    [InlineData("# T {onclick=alert(1)}")]
    public void GenericAttributes_AreNotRendered(string md) =>
        Assert.DoesNotContain(AttributeNames(R.RenderMarkdown(md)), n => n.StartsWith("on", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Markdown_NeverEmitsStyleAttributes() =>
        Assert.DoesNotContain("style=", R.RenderMarkdown("| a | b |\n|:-|-:|\n| 1 | 2 |"), StringComparison.Ordinal);

    [Fact]
    public void TableAlignment_BecomesClasses()
    {
        string html = R.RenderMarkdown("| a | b | c | d |\n|:-|:-:|-:|-|\n| 1 | 2 | 3 | 4 |");

        Assert.Contains("<th class=\"align-left\">a</th>", html, StringComparison.Ordinal);
        Assert.Contains("<th class=\"align-center\">b</th>", html, StringComparison.Ordinal);
        Assert.Contains("<td class=\"align-right\">3</td>", html, StringComparison.Ordinal);
        Assert.Contains("<td>4</td>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("style=", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Table_IsWrappedInScrollContainerAndKeepsTableElement()
    {
        string html = R.RenderMarkdown("| a | b |\n|---|---|\n| 1 | 2 |");

        Assert.StartsWith("<div class=\"table-scroll\" tabindex=\"0\">\n<table>\n", html, StringComparison.Ordinal);
        Assert.EndsWith("</table>\n</div>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoTables_GetOneWrapperEach()
    {
        string html = R.RenderMarkdown("| a |\n|---|\n| 1 |\n\ntext\n\n| b |\n|---|\n| 2 |");

        Assert.Equal(2, html.Split("<div class=\"table-scroll\" tabindex=\"0\">").Length - 1);
        Assert.Equal(2, html.Split("</table>\n</div>").Length - 1);
    }

    public static TheoryData<string, string> UnsafeCases => new()
    {
        { "[x](javascript:alert(1))", "javascript:alert(1)" },
        { "[x](javascript&#58;alert(1))", "javascript:alert(1)" },
        { "[x](java&#x09;script:alert(1))", "java\tscript:alert(1)" },
        { "<http://plain.dev>", "http://plain.dev" },
        { "![x](data:image/png;base64,AA)", "data:image/png;base64,AA" },
        { "[x][r]\n\n[r]: javascript:alert(1)", "javascript:alert(1)" },
        { "[x](/%2Fevil.com)", "/%2Fevil.com" },
        { "[x](/%5Cevil.com)", "/%5Cevil.com" },
        { "[x](//evil.com)", "//evil.com" },
        { "[![alt](https://a.dev/i.png)](javascript:alert(1))", "javascript:alert(1)" },
        { "[![alt](data:image/png;base64,AA)](https://a.dev)", "data:image/png;base64,AA" },
    };

    [Theory]
    [MemberData(nameof(UnsafeCases))]
    public void UnsafeLinks_AreRenderedAsText(string md, string expectedUnsafeTarget)
    {
        string html = R.RenderMarkdown(md);

        Assert.DoesNotContain("href=\"javascript", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=\"data:", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"http:", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"//", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/%", html, StringComparison.Ordinal);
        Assert.Equal([expectedUnsafeTarget], R.FindUnsafeLinks(md));
    }

    [Fact]
    public void ImageInsideUnsafeLink_KeepsSafeImage()
    {
        string html = R.RenderMarkdown("[![alt](https://a.dev/i.png)](javascript:alert(1))");

        Assert.Contains("<img src=\"https://a.dev/i.png\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<a ", html, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsafeImageInsideSafeLink_KeepsLinkAndDropsImage()
    {
        string html = R.RenderMarkdown("[![alt](data:image/png;base64,AA)](https://a.dev)");

        Assert.Contains("<a href=\"https://a.dev\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.Contains("alt", html, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsafeLink_KeepsLabelAsPlainText()
    {
        string html = R.RenderMarkdown("see [the **docs**](javascript:alert(1)) now");

        Assert.Equal("<p>see the <strong>docs</strong> now</p>", html.Trim());
    }

    [Fact]
    public void SafeLinksAndImages_ProduceNoFindings() =>
        Assert.Empty(R.FindUnsafeLinks("[a](https://a.dev) [b](/ru/x) ![c](https://a.dev/i.png)\n\n[r]: https://a.dev\n\n[d][r]"));

    [Fact]
    public void HttpsLink_GetsNofollowNoopenerNoreferrer() =>
        Assert.Contains("rel=\"nofollow noopener noreferrer\"", R.RenderMarkdown("[x](https://learn.microsoft.com)"), StringComparison.Ordinal);

    [Fact]
    public void HttpsAutolink_GetsNofollowNoopenerNoreferrer() =>
        Assert.Contains("rel=\"nofollow noopener noreferrer\"", R.RenderMarkdown("<https://learn.microsoft.com>"), StringComparison.Ordinal);

    [Fact]
    public void RelativeLink_IsKeptWithoutRel()
    {
        string html = R.RenderMarkdown("[x](/ru/dotnet)");

        Assert.Contains("href=\"/ru/dotnet\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("rel=", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Code_IsEncodedAndLineNumbered()
    {
        string html = R.RenderCode("var a = \"<b>\";\nint b = 1;", "csharp");

        Assert.Contains("&lt;b&gt;", html, StringComparison.Ordinal);
        Assert.Contains("data-line=\"2\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("style=", html, StringComparison.Ordinal);
        Assert.StartsWith("<pre class=\"code\"><code>", html, StringComparison.Ordinal);
        Assert.EndsWith("</code></pre>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Code_UnknownLanguage_IsEncodedAsPlainText()
    {
        string html = R.RenderCode("<script>alert(1)</script>\nx", "no-such-language");

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("data-line=\"2\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Code_CrLfAndTrailingNewline_DoNotAddLines()
    {
        string html = R.RenderCode("int a;\r\nint b;\r\n", "csharp");

        Assert.Contains("data-line=\"2\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-line=\"3\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', html);
    }

    [Fact]
    public void MultilineComment_KeepsSpansBalancedPerLine()
    {
        string html = R.RenderCode("/* a\n b */\nint c;", "csharp");

        string[] lines = html.Split('\n');
        Assert.Equal(3, lines.Length);
        foreach (string line in lines)
        {
            Assert.Equal(Count(line, "<span"), Count(line, "</span>"));
        }

        Assert.Contains("class=\"comment\"", lines[1], StringComparison.Ordinal);
    }

    private static string[] AttributeNames(string html) =>
        [.. TagRegex().Matches(html).SelectMany(tag => AttributeRegex().Matches(tag.Value).Select(m => m.Groups[1].Value))];

    private static int Count(string value, string token)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    [GeneratedRegex("""<[a-zA-Z][^>]*>""")]
    private static partial Regex TagRegex();

    [GeneratedRegex("""\s([a-zA-Z_:][\w:.-]*)\s*=""")]
    private static partial Regex AttributeRegex();
}
