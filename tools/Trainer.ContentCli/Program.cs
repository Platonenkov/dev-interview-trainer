using Trainer.Content.Reading;
using Trainer.Content.Rendering;
using Trainer.Content.Validation;
using Trainer.ContentCli.Commands;
using Trainer.ContentCli.Links;
using Trainer.Snippets;

using CancellationTokenSource cancellation = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

ContentReader reader = new();
DockerSnippetRunner runner = new();
ICliCommand[] commands =
[
    new ValidateCommand(reader, new ContentValidator(new ContentRenderer(), TimeProvider.System), Console.Out, Console.Error),
    new RunSnippetsCommand(reader, runner.PrepareAsync, new SnippetVerifier(runner), Console.Out, Console.Error),
    new CheckLinksCommand(
        reader, new LinkCollector(), LinkCheckHttpClient.Create, new LinkCheckOptions(),
        Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY"), Console.Out, Console.Error),
    new NewQuestionCommand(new QuestionTemplateBuilder(TimeProvider.System), Console.Out, Console.Error),
    new GenerateSchemasCommand(Console.Out, Console.Error),
];

ICliCommand? command = args.Length == 0
    ? null
    : commands.FirstOrDefault(c => string.Equals(c.Name, args[0], StringComparison.Ordinal));
if (command is null)
{
    Console.Error.WriteLine("Usage: Trainer.ContentCli <command> [arguments]");
    foreach (ICliCommand known in commands)
    {
        Console.Error.WriteLine($"  {known.Usage}");
    }

    return ExitCodes.UsageError;
}

return await command.ExecuteAsync(args.Skip(1).ToArray(), cancellation.Token);
