namespace Trainer.Content.Reading;

/// <summary>Issue codes produced while reading files; semantic validation has its own set.</summary>
internal static class ReadIssueCodes
{
    internal const string YamlParse = "yaml-parse";
    internal const string YamlExtension = "yaml-extension";
    internal const string YamlMultipleDocuments = "yaml-multiple-documents";
    internal const string YamlAliasForbidden = "yaml-alias-forbidden";
    internal const string ContentStructure = "content-structure";
    internal const string ContentFileMissing = "content-file-missing";
    internal const string SchemaVersionInvalid = "schema-version-invalid";
}
