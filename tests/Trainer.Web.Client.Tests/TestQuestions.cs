using Trainer.Contracts;

namespace Trainer.Web.Client.Tests;

/// <summary>Builds <see cref="QuestionDto"/> instances for the session model tests.</summary>
internal static class TestQuestions
{
    private static int _next;

    public static QuestionDto Single(string correct = "b", string? hint = "<p>hint</p>") =>
        Create("single", Options(("a", false), ("b", false), ("c", false), ("d", false)), hint: hint, correct: correct);

    public static QuestionDto Multi() =>
        Create("multi", Options(("a", true), ("b", true), ("c", false), ("d", false)));

    public static QuestionDto OutputChoice() =>
        Create("output", Options(("a", false), ("b", true), ("c", false)), lines: ["<span class=\"line\" data-line=\"1\">Console.Write(1);</span>"]);

    public static QuestionDto OutputTyped(params string[] accepted) =>
        Create("output", [], accepted: accepted.Length == 0 ? ["333"] : accepted, lines: ["<span class=\"line\" data-line=\"1\">Console.Write(333);</span>"]);

    public static QuestionDto FindBug() =>
        Create(
            "find-bug",
            [],
            lines:
            [
                "<span class=\"line\" data-line=\"1\">a</span>",
                "<span class=\"line\" data-line=\"2\">b</span>",
                "<span class=\"line\" data-line=\"3\">c</span>",
                "<span class=\"line\" data-line=\"4\">d</span>",
            ],
            bugLines: [[2], [3, 4]]);

    public static QuestionDto Open(string type = "open") => Create(type, []);

    public static string NewId()
    {
        int n = Interlocked.Increment(ref _next);
        return "01JC" + n.ToString("D22", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static List<OptionDto> Options(params (string Id, bool Correct)[] options) =>
        [.. options.Select(o => new OptionDto(o.Id, $"<p>{o.Id}</p>", o.Correct, $"<p>why {o.Id}</p>"))];

    private static QuestionDto Create(
        string type,
        IReadOnlyList<OptionDto> options,
        string? hint = "<p>hint</p>",
        IReadOnlyList<string>? accepted = null,
        IReadOnlyList<string>? lines = null,
        IReadOnlyList<IReadOnlyList<int>>? bugLines = null,
        string? correct = null)
    {
        if (correct is not null)
        {
            options = [.. options.Select(o => o with { Correct = string.Equals(o.Id, correct, StringComparison.Ordinal) })];
        }

        string id = NewId();
        return new QuestionDto(
            id,
            "slug-" + id.ToLowerInvariant(),
            "closures",
            "Closures",
            "middle",
            type,
            "Title " + id,
            "<p>prompt</p>",
            hint,
            lines,
            type == "find-bug" ? "<pre>fixed</pre>" : null,
            options,
            accepted,
            bugLines,
            "<p>answer</p>",
            "<p>explanation</p>",
            [new LinkDto("https://learn.microsoft.com/", "Docs", "en")]);
    }
}
