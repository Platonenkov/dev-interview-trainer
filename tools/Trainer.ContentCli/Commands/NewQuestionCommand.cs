using System.Text;
using Trainer.Domain.Keys;
using Trainer.Domain.Questions;

namespace Trainer.ContentCli.Commands;

internal sealed class NewQuestionCommand(QuestionTemplateBuilder templates, TextWriter output, TextWriter error) : ICliCommand
{
    public string Name => "new-question";

    public string Usage => "new-question <stack> <topic> <slug> <type> [root]";

    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count is < 4 or > 5)
        {
            error.WriteLine($"Usage: {Usage}");
            return ExitCodes.UsageError;
        }

        string? problem = FindArgumentProblem(args, out QuestionType type);
        if (problem is not null)
        {
            error.WriteLine(problem);
            return ExitCodes.UsageError;
        }

        string root = args.Count == 5 ? args[4] : ContentRoot.DefaultPath;
        string directory = Path.Combine(root, args[0], args[1]);
        string path = Path.Combine(directory, args[2] + ".yaml");
        if (File.Exists(path))
        {
            error.WriteLine($"'{path}' already exists; the id and slug of a question must never change.");
            return ExitCodes.UsageError;
        }

        Directory.CreateDirectory(directory);
        string text = templates.Build(Ulid.NewUlid().ToString(), args[0], args[1], args[2], type);
        await File.WriteAllBytesAsync(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text), ct);
        output.WriteLine($"Created {path}");
        return ExitCodes.Ok;
    }

    private static string? FindArgumentProblem(IReadOnlyList<string> args, out QuestionType type)
    {
        type = default;
        if (!StackKey.TryCreate(args[0], out _))
        {
            return $"'{args[0]}' is not a valid stack key (lowercase kebab-case).";
        }

        if (!TopicKey.TryCreate(args[1], out _))
        {
            return $"'{args[1]}' is not a valid topic key (lowercase kebab-case).";
        }

        if (!Slug.TryCreate(args[2], out _))
        {
            return $"'{args[2]}' is not a valid slug (lowercase kebab-case).";
        }

        return QuestionTypeKeys.TryParse(args[3], out type)
            ? null
            : $"'{args[3]}' is not a question type; use one of: {string.Join(", ", QuestionTypeKeys.All.Select(QuestionTypeKeys.ToKey))}.";
    }
}
