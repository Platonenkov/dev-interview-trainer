namespace Trainer.Domain.Grading;

/// <summary>Text normalisation shared by the content validator and the client-side output grader.</summary>
public static class OutputText
{
    /// <summary>Converts CRLF and CR to LF, then trims surrounding whitespace.</summary>
    public static string Normalise(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
}
