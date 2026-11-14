using System.Net;
using ColorCode;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Trainer.Content.Rendering;

/// <summary>
/// Renders untrusted content to HTML. Markdown goes through an explicit Markdig pipeline with raw HTML
/// disabled; every link and image target is checked against <see cref="SafeLinkPolicy"/> on the parsed tree.
/// </summary>
public sealed class ContentRenderer : IContentRenderer
{
    // Content arrives through public pull requests, so its external links must not pass ranking signals.
    private const string ExternalLinkRel = "nofollow noopener noreferrer";

    private readonly MarkdownPipeline _pipeline = MarkdownPipelineFactory.Create();

    public string RenderMarkdown(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        MarkdownDocument document = Markdown.Parse(markdown, _pipeline);
        SanitizeLinks(document, collected: null);
        ReplaceTableAlignmentStyles(document);

        using StringWriter writer = new();
        HtmlRenderer renderer = new(writer);
        _pipeline.Setup(renderer);
        renderer.ObjectRenderers.Replace<HtmlTableRenderer>(new ScrollableTableRenderer());
        renderer.Render(document);
        return writer.ToString();
    }

    public IReadOnlyList<string> FindUnsafeLinks(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        MarkdownDocument document = Markdown.Parse(markdown, _pipeline);
        List<string> unsafeTargets = [];
        SanitizeLinks(document, unsafeTargets);
        return unsafeTargets;
    }

    public string RenderCode(string source, string language) =>
        $"<pre class=\"code\"><code>{string.Join('\n', RenderCodeLines(source, language))}</code></pre>";

    public IReadOnlyList<string> RenderCodeLines(string source, string language)
    {
        ArgumentNullException.ThrowIfNull(source);

        string normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        ILanguage? colorCodeLanguage = string.IsNullOrWhiteSpace(language) ? null : Languages.FindById(language);
        string highlighted = colorCodeLanguage is null
            ? WebUtility.HtmlEncode(normalized)
            : ExtractHighlightedBody(new HtmlClassFormatter().GetHtmlString(normalized, colorCodeLanguage));

        return LineWrapper.Split(highlighted);
    }

    /// <summary>
    /// Replaces every unsafe link, image and autolink with its literal text and marks safe external links.
    /// When <paramref name="collected"/> is set, the unsafe targets are recorded in document order.
    /// </summary>
    private static void SanitizeLinks(MarkdownDocument document, List<string>? collected)
    {
        // Snapshot first: replacing an inline mutates the tree being walked.
        List<Inline> inlines = [.. document.Descendants<LinkInline>(), .. document.Descendants<AutolinkInline>()];

        foreach (Inline inline in inlines)
        {
            switch (inline)
            {
                case LinkInline link:
                    SanitizeLink(link, collected);
                    break;
                case AutolinkInline autolink:
                    SanitizeAutolink(autolink, collected);
                    break;
            }
        }
    }

    private static void SanitizeLink(LinkInline link, List<string>? collected)
    {
        // Reference links resolve their target lazily, and the renderer prefers that value.
        string url = link.GetDynamicUrl?.Invoke() ?? link.Url ?? string.Empty;

        if (!SafeLinkPolicy.IsAllowed(url))
        {
            collected?.Add(url);
            UnwrapLink(link);
            return;
        }

        if (!link.IsImage && SafeLinkPolicy.IsAllowedAbsolute(url))
        {
            link.GetAttributes().AddPropertyIfNotExist("rel", ExternalLinkRel);
        }
    }

    private static void SanitizeAutolink(AutolinkInline autolink, List<string>? collected)
    {
        // Email autolinks render as mailto: links, which the policy does not allow.
        string target = autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url;

        if (SafeLinkPolicy.IsAllowed(target))
        {
            if (SafeLinkPolicy.IsAllowedAbsolute(target))
            {
                autolink.GetAttributes().AddPropertyIfNotExist("rel", ExternalLinkRel);
            }

            return;
        }

        collected?.Add(target);
        autolink.ReplaceBy(new LiteralInline(autolink.Url));
    }

    /// <summary>Replaces a link with its label content, which keeps formatting and nested safe images.</summary>
    private static void UnwrapLink(LinkInline link)
    {
        while (link.FirstChild is { } child)
        {
            child.Remove();
            link.InsertBefore(child);
        }

        link.Remove();
    }

    /// <summary>
    /// Markdig writes table alignment as an inline <c>style</c> attribute, which the CSP forbids.
    /// The alignment is moved to a class on each cell and removed from the column definition.
    /// </summary>
    private static void ReplaceTableAlignmentStyles(MarkdownDocument document)
    {
        foreach (Table table in document.Descendants<Table>())
        {
            foreach (TableRow row in table.OfType<TableRow>())
            {
                for (int column = 0; column < row.Count; column++)
                {
                    if (row[column] is not TableCell cell || column >= table.ColumnDefinitions.Count)
                    {
                        continue;
                    }

                    string? alignmentClass = table.ColumnDefinitions[column].Alignment switch
                    {
                        TableColumnAlign.Left => "align-left",
                        TableColumnAlign.Center => "align-center",
                        TableColumnAlign.Right => "align-right",
                        _ => null,
                    };

                    if (alignmentClass is not null)
                    {
                        cell.GetAttributes().AddClass(alignmentClass);
                    }
                }
            }

            foreach (TableColumnDefinition definition in table.ColumnDefinitions)
            {
                definition.Alignment = null;
            }
        }
    }

    /// <summary>Strips the wrapper that ColorCode adds around the highlighted text.</summary>
    private static string ExtractHighlightedBody(string formatterHtml)
    {
        const string preOpen = "<pre>";
        const string preClose = "</pre>";

        // ColorCode emits Environment.NewLine, which is CRLF on Windows.
        formatterHtml = formatterHtml.Replace("\r\n", "\n", StringComparison.Ordinal);

        int start = formatterHtml.IndexOf(preOpen, StringComparison.Ordinal);
        int end = formatterHtml.LastIndexOf(preClose, StringComparison.Ordinal);
        if (start < 0 || end < start)
        {
            throw new InvalidOperationException("Unexpected ColorCode output: <pre> wrapper not found.");
        }

        string body = formatterHtml.Substring(start + preOpen.Length, end - start - preOpen.Length);

        // ColorCode puts a newline after <pre> and before </pre>; neither belongs to the source.
        if (body.StartsWith('\n'))
        {
            body = body[1..];
        }

        if (body.EndsWith('\n'))
        {
            body = body[..^1];
        }

        return body;
    }
}
