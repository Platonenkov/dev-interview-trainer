namespace Trainer.Application.Content;

/// <summary>The content format versions this build reads and renders, so the web layer can report them without referencing the content library.</summary>
public interface IContentSchemaInfo
{
    int MinSupported { get; }

    int MaxSupported { get; }

    /// <summary>Bumped whenever rendering or the stored payload format changes.</summary>
    int RenderVersion { get; }
}
