using Markdig.Extensions.Tables;
using Markdig.Renderers;

namespace Trainer.Content.Rendering;

/// <summary>
/// Wraps every table in a scroll container. Making the table itself scrollable (<c>display: block</c>)
/// drops its table semantics for assistive technology, so the wrapper scrolls instead.
/// </summary>
internal sealed class ScrollableTableRenderer : HtmlTableRenderer
{
    protected override void Write(HtmlRenderer renderer, Table table)
    {
        if (!renderer.EnableHtmlForBlock)
        {
            base.Write(renderer, table);
            return;
        }

        // tabindex lets keyboard users scroll a wide table that contains no focusable cell content.
        renderer.Write("<div class=\"table-scroll\" tabindex=\"0\">");
        base.Write(renderer, table);
        renderer.Write("</div>");
    }
}
