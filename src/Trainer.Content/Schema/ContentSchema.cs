namespace Trainer.Content.Schema;

/// <summary>Version numbers that tie the content format to the application that reads and renders it.</summary>
public static class ContentSchema
{
    public const int MinSupported = 1;
    public const int MaxSupported = 1;

    /// <summary>Bump whenever rendering or the stored payload format changes; the importer compares (hash, render version).</summary>
    public const int RenderVersion = 2;
}
