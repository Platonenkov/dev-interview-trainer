using System.Globalization;
using System.Text;
using Trainer.Domain.Questions;

namespace Trainer.ContentCli.Commands;

/// <summary>Builds the skeleton of a new question file. The skeleton parses and satisfies the structural rules, but its text must be replaced before merge.</summary>
internal sealed class QuestionTemplateBuilder(TimeProvider time)
{
    private const string Placeholder = "Replace this text";

    public string Build(string ulid, string stack, string topic, string slug, QuestionType type)
    {
        bool selfGraded = type is QuestionType.Open or QuestionType.Situation;
        DateOnly today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

        StringBuilder sb = new();
        sb.Append("# yaml-language-server: $schema=../../schema/question.schema.json\n");
        sb.Append(CultureInfo.InvariantCulture, $"id: {ulid}\n");
        sb.Append(CultureInfo.InvariantCulture, $"slug: {slug}\n");
        sb.Append(CultureInfo.InvariantCulture, $"stack: {stack}\n");
        sb.Append(CultureInfo.InvariantCulture, $"topic: {topic}\n");
        sb.Append("level: middle\n");
        sb.Append(CultureInfo.InvariantCulture, $"type: {QuestionTypeKeys.ToKey(type)}\n");
        sb.Append(CultureInfo.InvariantCulture, $"grading: {(selfGraded ? "self" : "auto")}\n");
        sb.Append(CultureInfo.InvariantCulture, $"tags: [{topic}]\n");
        AppendTypeBody(sb, type);
        sb.Append("links:\n");
        sb.Append("  - url: https://learn.microsoft.com/en-us/dotnet/\n");
        sb.Append("    lang: en\n");
        sb.Append("    kind: docs\n");
        sb.Append("locales:\n");
        AppendLocale(sb, "ru", type);
        AppendLocale(sb, "en", type);
        sb.Append("meta:\n");
        sb.Append("  authors: [replace-with-author]\n");
        sb.Append(CultureInfo.InvariantCulture, $"  verified: {today:yyyy-MM-dd}\n");
        sb.Append("  dotnetVersion: \"10\"\n");
        return sb.ToString();
    }

    private static void AppendTypeBody(StringBuilder sb, QuestionType type)
    {
        switch (type)
        {
            case QuestionType.Single:
                AppendOptions(sb, ["a", "b", "c"], ["b"], withExpect: false);
                break;
            case QuestionType.Multi:
                AppendOptions(sb, ["a", "b", "c", "d"], ["a", "c"], withExpect: false);
                break;
            case QuestionType.Output:
                sb.Append("code:\n  language: csharp\n  harness: statements\n  source: |\n    Console.Write(1);\n");
                sb.Append("expect:\n  kind: output\n  value: \"1\"\n");
                sb.Append("acceptedOutputs: [\"1\"]\n");
                AppendOptions(sb, ["a", "b", "c"], ["a"], withExpect: true);
                break;
            case QuestionType.FindBug:
                sb.Append("code:\n  language: csharp\n  harness: statements\n  source: |\n    int x = 1;\n    Console.Write(x);\n");
                sb.Append("bugLineSets: [[1]]\n");
                sb.Append("fixedSource: |\n  int x = 2;\n  Console.Write(x);\n");
                sb.Append("fixedExpect: { kind: output, value: \"2\" }\n");
                break;
        }
    }

    private static void AppendOptions(StringBuilder sb, string[] ids, string[] correct, bool withExpect)
    {
        sb.Append("options:\n");
        for (int i = 0; i < ids.Length; i++)
        {
            bool isCorrect = correct.Contains(ids[i]);
            sb.Append(CultureInfo.InvariantCulture, $"  - id: {ids[i]}\n");
            sb.Append(CultureInfo.InvariantCulture, $"    correct: {(isCorrect ? "true" : "false")}\n");
            if (withExpect)
            {
                string value = isCorrect ? "1" : (i + 1).ToString(CultureInfo.InvariantCulture);
                sb.Append(CultureInfo.InvariantCulture, $"    expect: {{ kind: output, value: \"{value}\" }}\n");
            }
        }
    }

    private static void AppendLocale(StringBuilder sb, string culture, QuestionType type)
    {
        sb.Append(CultureInfo.InvariantCulture, $"  {culture}:\n");
        sb.Append(CultureInfo.InvariantCulture, $"    title: {Placeholder}\n");
        sb.Append(CultureInfo.InvariantCulture, $"    prompt: |\n      {Placeholder}\n");
        sb.Append(CultureInfo.InvariantCulture, $"    hint: {Placeholder}\n");
        if (type is QuestionType.Single or QuestionType.Multi or QuestionType.Output)
        {
            AppendLocaleOptions(sb, type);
        }

        sb.Append(CultureInfo.InvariantCulture, $"    answer: |\n      {Placeholder}\n");
        sb.Append(CultureInfo.InvariantCulture, $"    explanation: |\n      {Placeholder}\n");
        sb.Append("    linkTitles:\n      - Replace with the link title\n");
    }

    private static void AppendLocaleOptions(StringBuilder sb, QuestionType type)
    {
        string[] ids = type == QuestionType.Multi ? ["a", "b", "c", "d"] : ["a", "b", "c"];
        string[] correct = type switch
        {
            QuestionType.Multi => ["a", "c"],
            QuestionType.Output => ["a"],
            _ => ["b"],
        };

        sb.Append("    options:\n");
        foreach (string id in ids)
        {
            sb.Append(CultureInfo.InvariantCulture, $"      {id}: {Placeholder}\n");
        }

        sb.Append("    optionExplanations:\n");
        foreach (string id in ids.Where(id => !correct.Contains(id)))
        {
            sb.Append(CultureInfo.InvariantCulture, $"      {id}: {Placeholder}\n");
        }
    }
}
