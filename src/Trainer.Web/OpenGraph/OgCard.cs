namespace Trainer.Web.OpenGraph;

/// <summary>Everything an Open Graph card shows; two equal cards render to the same bytes.</summary>
/// <param name="Culture">The card language, <c>ru</c> or <c>en</c>.</param>
/// <param name="LevelKey">The lowercase level key, or null for the site card.</param>
/// <param name="TopicTitle">The localised topic title, or null for the site card.</param>
/// <param name="Footer">The localised site name and the public host.</param>
public sealed record OgCard(string Culture, string Title, string? LevelKey, string? TopicTitle, string Footer);
