using System.Text.Json;

namespace Trainer.Web.Client.Storage;

internal enum StoredKind
{
    Missing,
    Valid,
    Corrupt,
    NewerVersion,
}

/// <summary>
/// Classifies a raw stored value with the same rules as <c>classify</c> in <c>outbox.js</c>: unparseable JSON (including
/// nesting deeper than <see cref="MaxDepth"/>), a non-object, a missing
/// or non-integer <c>version</c>, or a version-1 document whose <c>answers</c> or <c>items</c> member is not an array or
/// whose <c>revision</c> is not an integer is corrupt; any other version belongs to a newer app.
/// </summary>
internal readonly record struct StoredDocument(StoredKind Kind, JsonElement Root)
{
    public const int Version = 1;

    /// <summary>The deepest nesting a stored document may have; <c>outbox.js</c> applies the same limit.</summary>
    public const int MaxDepth = 64;

    private static readonly JsonDocumentOptions Options = new() { MaxDepth = MaxDepth };

    private static readonly string[] ListMembers = ["answers", "items"];

    public static StoredDocument Parse(string? raw)
    {
        if (raw is null)
        {
            return new StoredDocument(StoredKind.Missing, default);
        }

        JsonElement root;
        try
        {
            using JsonDocument document = JsonDocument.Parse(raw, Options);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new StoredDocument(StoredKind.Corrupt, default);
        }

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("version", out JsonElement version)
            || version.ValueKind != JsonValueKind.Number
            || !IsInteger(version.GetDouble()))
        {
            return new StoredDocument(StoredKind.Corrupt, default);
        }

        if (version.GetDouble() != Version)
        {
            return new StoredDocument(StoredKind.NewerVersion, default);
        }

        foreach (string member in ListMembers)
        {
            if (root.TryGetProperty(member, out JsonElement list) && list.ValueKind != JsonValueKind.Array)
            {
                return new StoredDocument(StoredKind.Corrupt, default);
            }
        }

        if (root.TryGetProperty("revision", out JsonElement revision)
            && (revision.ValueKind != JsonValueKind.Number || !IsInteger(revision.GetDouble())))
        {
            return new StoredDocument(StoredKind.Corrupt, default);
        }

        return new StoredDocument(StoredKind.Valid, root);
    }

    /// <summary>The elements of a list member of a valid document; empty for any other kind or a missing member.</summary>
    public IEnumerable<JsonElement> Items(string member) =>
        Kind == StoredKind.Valid && Root.TryGetProperty(member, out JsonElement list) ? list.EnumerateArray() : [];

    /// <summary>The string <paramref name="idMember"/> of every list entry that has one, valid or not.</summary>
    public List<string> Ids(string member, string idMember) =>
        [.. Items(member)
            .Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(idMember, out JsonElement id) && id.ValueKind == JsonValueKind.String)
            .Select(e => e.GetProperty(idMember).GetString()!)];

    private static bool IsInteger(double value) => double.IsFinite(value) && Math.Floor(value) == value;
}
