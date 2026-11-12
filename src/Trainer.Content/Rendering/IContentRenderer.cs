namespace Trainer.Content.Rendering;

/// <summary>Renders untrusted content Markdown and code to HTML that is safe to emit unescaped.</summary>
public interface IContentRenderer
{
    /// <summary>Renders Markdown with raw HTML disabled and unsafe links replaced by their literal text.</summary>
    string RenderMarkdown(string markdown);

    /// <summary>Renders a code block with CSS-class highlighting; every line is wrapped in its own span.</summary>
    string RenderCode(string source, string language);

    /// <summary>Returns the link and image targets in the Markdown that <see cref="SafeLinkPolicy"/> rejects.</summary>
    IReadOnlyList<string> FindUnsafeLinks(string markdown);
}
