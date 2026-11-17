using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Trainer.Content.Model;
using Trainer.Content.Validation;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Trainer.Content.Reading;

/// <summary>
/// Reads a content root: <c>schema-version</c>, <c>stacks.yaml</c> and, per stack directory, <c>topics.yaml</c>,
/// optional <c>redirects.yaml</c> and question files. Reading never stops at the first bad file.
/// </summary>
public sealed class ContentReader : IContentReader
{
    private const string SchemaDirectory = "schema";
    private const string SchemaVersionFile = "schema-version";
    private const string StacksFile = "stacks.yaml";
    private const string TopicsFile = "topics.yaml";
    private const string RedirectsFile = "redirects.yaml";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Func<CancellationToken, Task>? _afterRootResolved;

    public ContentReader()
    {
    }

    /// <summary>Test seam: runs right after the root is resolved, before any file is listed or read.</summary>
    internal ContentReader(Func<CancellationToken, Task> afterRootResolved)
    {
        _afterRootResolved = afterRootResolved;
    }

    public async Task<ContentSet> ReadAsync(string rootPath, CancellationToken ct)
    {
        string root = ResolveRoot(Path.GetFullPath(rootPath));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Content root '{root}' does not exist.");
        }

        if (_afterRootResolved is not null)
        {
            await _afterRootResolved(ct);
        }

        List<ContentIssue> issues = [];
        Dictionary<string, byte[]> files = await LoadFilesAsync(root, issues, ct);

        int schemaVersion = ReadSchemaVersion(files, issues);
        IReadOnlyList<StackDocument> stacks = ReadDocument(StacksFile, files, ContentJsonContext.Default.IReadOnlyListStackDocument, issues, required: true) ?? [];

        Dictionary<string, IReadOnlyList<TopicDocument>> topicsByStack = new(StringComparer.Ordinal);
        Dictionary<string, IReadOnlyList<RedirectDocument>> redirectsByStack = new(StringComparer.Ordinal);
        foreach (string stack in StackDirectories(root))
        {
            IReadOnlyList<TopicDocument>? topics = ReadDocument($"{stack}/{TopicsFile}", files, ContentJsonContext.Default.IReadOnlyListTopicDocument, issues, required: true);
            if (topics is not null)
            {
                topicsByStack[stack] = topics;
            }

            IReadOnlyList<RedirectDocument>? redirects = ReadDocument($"{stack}/{RedirectsFile}", files, ContentJsonContext.Default.IReadOnlyListRedirectDocument, issues, required: false);
            if (redirects is not null)
            {
                redirectsByStack[stack] = redirects;
            }
        }

        List<SourcedQuestion> questions = [];
        foreach (string path in files.Keys.Order(StringComparer.Ordinal).Where(IsQuestionPath))
        {
            QuestionDocument? document = ReadDocument(path, files, ContentJsonContext.Default.QuestionDocument, issues, required: true);
            if (document is not null)
            {
                questions.Add(new SourcedQuestion(path, document));
            }
        }

        string hash = ContentHasher.Compute(files.Select(f => (f.Key, f.Value)));
        return new ContentSet(schemaVersion, stacks, topicsByStack, questions, redirectsByStack, hash, issues);
    }

    /// <summary>
    /// The deployment switches <c>/content/current</c> to a new release directory; resolving the link once pins this read
    /// to a single release, so the files read, their hash and the enumeration can never mix two releases.
    /// </summary>
    private static string ResolveRoot(string root)
    {
        FileSystemInfo? target = Directory.ResolveLinkTarget(root, returnFinalTarget: true);
        return target is null ? root : target.FullName;
    }

    private static bool IsQuestionPath(string path)
    {
        string[] segments = path.Split('/');
        if (segments.Length < 2)
        {
            return false;
        }

        // Stack-level topics and redirects files sit directly under the stack directory.
        return segments.Length != 2 || (segments[1] != TopicsFile && segments[1] != RedirectsFile);
    }

    private static IEnumerable<string> StackDirectories(string root) =>
        Directory.EnumerateDirectories(root)
            .Select(d => Path.GetFileName(d))
            .Where(name => name != SchemaDirectory && !name.StartsWith('.'))
            .Order(StringComparer.Ordinal);

    private static async Task<Dictionary<string, byte[]>> LoadFilesAsync(string root, List<ContentIssue> issues, CancellationToken ct)
    {
        List<string> paths = [];
        string schemaVersionPath = Path.Combine(root, SchemaVersionFile);
        if (File.Exists(schemaVersionPath))
        {
            paths.Add(schemaVersionPath);
        }

        paths.AddRange(Directory.EnumerateFiles(root, "*.yaml", SearchOption.TopDirectoryOnly));
        foreach (string stack in StackDirectories(root))
        {
            paths.AddRange(Directory.EnumerateFiles(Path.Combine(root, stack), "*.yaml", SearchOption.AllDirectories));
        }

        // The reader only knows .yaml; a .yml file would otherwise be dropped without a trace.
        List<string> wrongExtension = [];
        wrongExtension.AddRange(Directory.EnumerateFiles(root, "*.yml", SearchOption.TopDirectoryOnly));
        foreach (string stack in StackDirectories(root))
        {
            wrongExtension.AddRange(Directory.EnumerateFiles(Path.Combine(root, stack), "*.yml", SearchOption.AllDirectories));
        }

        foreach (string path in wrongExtension.Where(p => p.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            issues.Add(new ContentIssue(Path.GetRelativePath(root, path).Replace('\\', '/'), ReadIssueCodes.YamlExtension, "Content files use the .yaml extension; rename the file."));
        }

        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            files[relative] = await File.ReadAllBytesAsync(path, ct);
        }

        return files;
    }

    private static int ReadSchemaVersion(Dictionary<string, byte[]> files, List<ContentIssue> issues)
    {
        if (!files.TryGetValue(SchemaVersionFile, out byte[]? bytes))
        {
            issues.Add(new ContentIssue(SchemaVersionFile, ReadIssueCodes.ContentFileMissing, "The schema-version file is missing."));
            return 0;
        }

        string text = Encoding.UTF8.GetString(ContentBytes.Normalize(bytes)).Trim();
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int version))
        {
            return version;
        }

        issues.Add(new ContentIssue(SchemaVersionFile, ReadIssueCodes.SchemaVersionInvalid, "The schema-version file must contain a single non-negative integer."));
        return 0;
    }

    private static T? ReadDocument<T>(string path, Dictionary<string, byte[]> files, JsonTypeInfo<T> typeInfo, List<ContentIssue> issues, bool required)
        where T : class
    {
        if (!files.TryGetValue(path, out byte[]? bytes))
        {
            if (required)
            {
                issues.Add(new ContentIssue(path, ReadIssueCodes.ContentFileMissing, "The file is missing."));
            }

            return null;
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(ContentBytes.Normalize(bytes));
        }
        catch (DecoderFallbackException)
        {
            issues.Add(new ContentIssue(path, ReadIssueCodes.ContentStructure, "The file is not valid UTF-8."));
            return null;
        }

        YamlStream stream = new();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException ex)
        {
            issues.Add(new ContentIssue(path, ReadIssueCodes.YamlParse, $"{ex.Message} (line {ex.Start.Line}, column {ex.Start.Column})"));
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // YamlDotNet scanner and mapping loader leak these for some malformed input (unterminated flow
            // collections, duplicate keys) instead of a positioned YamlException.
            issues.Add(new ContentIssue(path, ReadIssueCodes.YamlParse, ex.Message));
            return null;
        }

        JsonNode? node = YamlToJson.Convert(stream, out IReadOnlyList<ContentIssue> conversionIssues, path);
        if (conversionIssues.Count > 0)
        {
            issues.AddRange(conversionIssues);
            return null;
        }

        if (node is null)
        {
            issues.Add(new ContentIssue(path, ReadIssueCodes.ContentStructure, "The document is empty."));
            return null;
        }

        try
        {
            T? result = JsonSerializer.Deserialize(node, typeInfo);
            if (result is null)
            {
                issues.Add(new ContentIssue(path, ReadIssueCodes.ContentStructure, "The document is empty."));
            }

            return result;
        }
        catch (JsonException ex)
        {
            issues.Add(new ContentIssue(path, ReadIssueCodes.ContentStructure, ex.Message, ex.Path));
            return null;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            issues.Add(new ContentIssue(path, ReadIssueCodes.ContentStructure, ex.Message));
            return null;
        }
    }
}
