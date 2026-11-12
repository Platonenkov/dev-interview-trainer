namespace Trainer.Content.Validation;

/// <summary>A problem found in a content file. <paramref name="JsonPath"/> points into the document when known.</summary>
public sealed record ContentIssue(string RelativePath, string Code, string Message, string? JsonPath = null);
