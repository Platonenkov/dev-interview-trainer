namespace Trainer.Application.Content;

/// <summary>A reason an import was rejected. <paramref name="Path"/> is the content-root-relative file, <paramref name="Code"/> a stable rule code.</summary>
public sealed record ImportIssue(string Path, string Code, string Message);
