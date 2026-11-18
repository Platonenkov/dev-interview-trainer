namespace Trainer.Contracts;

/// <summary>
/// The per-culture, per-stack index of the static edition's content shards (ADR 0023). It is the one file a page needs
/// to know which question files exist; everything in it is derived from the imported content, so identical content gives
/// byte-identical JSON.
/// </summary>
/// <param name="Format">Bumped when the shape changes, so an old client can tell it is looking at a newer file.</param>
/// <param name="ContentHash">The hash of the content import the shards were built from.</param>
/// <param name="Questions">One entry per live question with a locale for <paramref name="Culture"/>, ordered by id (ordinal).</param>
public sealed record StaticIndexDto(
    int Format,
    string Culture,
    string Stack,
    string ContentHash,
    IReadOnlyList<TopicDto> Topics,
    IReadOnlyList<StaticIndexEntryDto> Questions)
{
    public const int CurrentFormat = 1;
}

/// <param name="Hash">The 16-character content hash that is part of the question file's name.</param>
public sealed record StaticIndexEntryDto(string Id, string Slug, string Topic, string Level, string Type, string Title, string Hash);

/// <summary>What a static-edition island page tells its island about the content shards and the project's issue form.</summary>
/// <param name="IndexPath">Relative to the site's base URL, without a leading slash.</param>
/// <param name="IssuesNewUrl">The repository's "new issue" URL that "Report a problem" links to.</param>
public sealed record StaticSiteDto(string IndexPath, string IssuesNewUrl);

/// <summary>Site-relative paths of the content shards; the host writes them and the island derives them from the index.</summary>
public static class StaticContentPaths
{
    /// <summary>The question file <c>data/{culture}/q/{id}.{hash}.json</c>.</summary>
    public static string Question(string culture, string id, string hash) => $"data/{culture}/q/{id}.{hash}.json";

    /// <summary>The index file <c>data/{culture}/{stack}/index.{hash}.json</c>.</summary>
    public static string Index(string culture, string stack, string hash) => $"data/{culture}/{stack}/index.{hash}.json";
}
