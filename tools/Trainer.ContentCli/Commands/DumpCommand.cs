using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Trainer.Content.Model;
using Trainer.Content.Reading;
using Trainer.Content.Validation;

namespace Trainer.ContentCli.Commands;

/// <summary>
/// Prints the parsed content as one JSON object so external tooling never parses YAML itself:
/// the C# reader stays the single source of truth for what a file means.
/// </summary>
internal sealed class DumpCommand(IContentReader reader, TextWriter output, TextWriter error) : ICliCommand
{
    // Relaxed escaping keeps Cyrillic readable in the output; the JSON is consumed by local tooling, never embedded in HTML.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public string Name => "dump";

    public string Usage => "dump [root]";

    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        string? root = ContentRoot.Resolve(args);
        if (root is null)
        {
            error.WriteLine($"Usage: {Usage}");
            return ExitCodes.UsageError;
        }

        ContentSet set;
        try
        {
            set = await reader.ReadAsync(root, ct);
        }
        catch (DirectoryNotFoundException ex)
        {
            error.WriteLine(ex.Message);
            return ExitCodes.UsageError;
        }

        List<DumpedQuestion> questions = new(set.Questions.Count);
        foreach (SourcedQuestion question in set.Questions)
        {
            string sha = await HashFileAsync(Path.Combine(root, question.RelativePath), ct);
            questions.Add(new DumpedQuestion(question.RelativePath, sha, question.Document));
        }

        List<DumpedIssue> readIssues = set.ReadIssues
            .Select(issue => new DumpedIssue(issue.RelativePath, issue.Code, issue.Message, issue.JsonPath))
            .ToList();

        // Read issues are data for the caller, not a failure of the command, so the exit code stays 0.
        DumpResult result = new(set.Hash, questions, set.TopicsByStack, set.RedirectsByStack, readIssues);
        output.WriteLine(JsonSerializer.Serialize(result, SerializerOptions));
        return ExitCodes.Ok;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path, ct);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private sealed record DumpResult(
        string Hash,
        IReadOnlyList<DumpedQuestion> Questions,
        IReadOnlyDictionary<string, IReadOnlyList<TopicDocument>> Topics,
        IReadOnlyDictionary<string, IReadOnlyList<RedirectDocument>> Redirects,
        IReadOnlyList<DumpedIssue> ReadIssues);

    private sealed record DumpedQuestion(string Path, string Sha256, QuestionDocument Document);

    private sealed record DumpedIssue(string Path, string Code, string Message, string? Pointer);
}
