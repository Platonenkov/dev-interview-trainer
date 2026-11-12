using Markdig;
using Markdig.Extensions.Tables;

namespace Trainer.Content.Rendering;

/// <summary>
/// Builds the single Markdig pipeline used for untrusted content. Extensions that add raw HTML,
/// generic attributes, media embeds, auto-links, math or diagrams are deliberately absent.
/// </summary>
public static class MarkdownPipelineFactory
{
    public static MarkdownPipeline Create() =>
        new MarkdownPipelineBuilder()
            .UsePipeTables(new PipeTableOptions { InferColumnWidthsFromSeparator = false })
            .UseEmphasisExtras()
            .UseListExtras()
            .UseAutoIdentifiers()
            .DisableHtml()
            .Build();
}
