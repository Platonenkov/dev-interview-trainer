using System.Text;
using Trainer.Content.Schema;

namespace Trainer.ContentCli.Commands;

internal sealed class GenerateSchemasCommand(TextWriter output, TextWriter error) : ICliCommand
{
    public string Name => "generate-schemas";

    public string Usage => "generate-schemas [root]";

    public async Task<int> ExecuteAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        string? root = ContentRoot.Resolve(args);
        if (root is null)
        {
            error.WriteLine($"Usage: {Usage}");
            return ExitCodes.UsageError;
        }

        if (!Directory.Exists(root))
        {
            error.WriteLine($"Content root '{root}' does not exist.");
            return ExitCodes.UsageError;
        }

        string directory = Path.Combine(root, "schema");
        Directory.CreateDirectory(directory);

        // The generator already produces LF text; writing bytes avoids any platform newline or BOM translation.
        UTF8Encoding utf8 = new(encoderShouldEmitUTF8Identifier: false);
        foreach ((string name, string text) in SchemaGenerator.Generate())
        {
            string path = Path.Combine(directory, name);
            await File.WriteAllBytesAsync(path, utf8.GetBytes(text), ct);
            output.WriteLine($"Wrote {path}");
        }

        return ExitCodes.Ok;
    }
}
