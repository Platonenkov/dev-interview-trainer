using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Trainer.Content.Validation;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Trainer.Content.Reading;

/// <summary>
/// Converts a parsed YAML stream to a JSON tree with deliberately narrow scalar typing, so that values such as
/// <c>012</c>, <c>1.5</c> or <c>2026-11-11</c> stay text instead of being guessed into numbers or dates.
/// </summary>
public static partial class YamlToJson
{
    private const int MaxDepth = 64;

    // The end anchor is \z because a plain dollar would also accept a trailing newline.
    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerPattern();

    /// <summary>
    /// Returns the JSON tree of the single document in <paramref name="stream"/>, or null when
    /// <paramref name="issues"/> is not empty. Rejects multiple documents, anchors, aliases, merge keys, tags
    /// and non-scalar mapping keys.
    /// </summary>
    public static JsonNode? Convert(YamlStream stream, out IReadOnlyList<ContentIssue> issues, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(stream);

        List<ContentIssue> found = [];
        issues = found;

        if (stream.Documents.Count != 1)
        {
            found.Add(stream.Documents.Count == 0
                ? new ContentIssue(relativePath, ReadIssueCodes.ContentStructure, "The file contains no YAML document.")
                : new ContentIssue(relativePath, ReadIssueCodes.YamlMultipleDocuments, $"The file contains {stream.Documents.Count} YAML documents; exactly one is allowed."));
            return null;
        }

        JsonNode? root = ConvertNode(stream.Documents[0].RootNode, relativePath, found, 0);
        // An aliased node is the same object as its anchor, so every use would repeat the report.
        bool aliasReported = false;
        found.RemoveAll(issue =>
        {
            if (issue.Code != ReadIssueCodes.YamlAliasForbidden)
            {
                return false;
            }

            bool duplicate = aliasReported;
            aliasReported = true;
            return duplicate;
        });

        return found.Count == 0 ? root : null;
    }

    private static JsonNode? ConvertNode(YamlNode node, string path, List<ContentIssue> issues, int depth)
    {
        if (depth > MaxDepth)
        {
            issues.Add(Issue(path, ReadIssueCodes.ContentStructure, node, "The document is nested too deeply."));
            return null;
        }

        if (!node.Anchor.IsEmpty)
        {
            issues.Add(Issue(path, ReadIssueCodes.YamlAliasForbidden, node, "Anchors and aliases are not allowed in content files."));
            return null;
        }

        if (!node.Tag.IsEmpty)
        {
            issues.Add(Issue(path, ReadIssueCodes.ContentStructure, node, $"Explicit YAML tag '{node.Tag}' is not allowed."));
            return null;
        }

        switch (node)
        {
            case YamlScalarNode scalar:
                return ConvertScalar(scalar);
            case YamlSequenceNode sequence:
                JsonArray array = [];
                foreach (YamlNode item in sequence.Children)
                {
                    array.Add(ConvertNode(item, path, issues, depth + 1));
                }

                return array;
            case YamlMappingNode mapping:
                return ConvertMapping(mapping, path, issues, depth);
            default:
                issues.Add(Issue(path, ReadIssueCodes.YamlAliasForbidden, node, "Anchors and aliases are not allowed in content files."));
                return null;
        }
    }

    private static JsonObject ConvertMapping(YamlMappingNode mapping, string path, List<ContentIssue> issues, int depth)
    {
        JsonObject result = [];
        foreach ((YamlNode keyNode, YamlNode valueNode) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode key || key.Value is null)
            {
                issues.Add(Issue(path, ReadIssueCodes.ContentStructure, keyNode, "Mapping keys must be plain strings."));
                continue;
            }

            if (key.Style == ScalarStyle.Plain && key.Value == "<<")
            {
                issues.Add(Issue(path, ReadIssueCodes.YamlAliasForbidden, keyNode, "Merge keys are not allowed in content files."));
                continue;
            }

            if (!key.Anchor.IsEmpty || !key.Tag.IsEmpty)
            {
                issues.Add(Issue(path, ReadIssueCodes.YamlAliasForbidden, keyNode, "Anchors, aliases and tags are not allowed on keys."));
                continue;
            }

            result[key.Value] = ConvertNode(valueNode, path, issues, depth + 1);
        }

        return result;
    }

    private static JsonValue? ConvertScalar(YamlScalarNode scalar)
    {
        string text = scalar.Value ?? string.Empty;
        if (scalar.Style != ScalarStyle.Plain)
        {
            return JsonValue.Create(text);
        }

        switch (text)
        {
            case "true":
                return JsonValue.Create(true);
            case "false":
                return JsonValue.Create(false);
            case "" or "null" or "~":
                return null;
        }

        if (IntegerPattern().IsMatch(text) && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number))
        {
            // int when it fits, so GetValue<int>() works for the usual small numbers.
            return number is >= int.MinValue and <= int.MaxValue ? JsonValue.Create((int)number) : JsonValue.Create(number);
        }

        return JsonValue.Create(text);
    }

    private static ContentIssue Issue(string path, string code, YamlNode node, string message) =>
        new(path, code, $"{message} (line {node.Start.Line}, column {node.Start.Column})");
}
