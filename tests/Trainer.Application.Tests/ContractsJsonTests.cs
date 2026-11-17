using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Trainer.Contracts;
using Trainer.Contracts.Storage;

namespace Trainer.Application.Tests;

public sealed class ContractsJsonTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 12, 10, 30, 0, TimeSpan.Zero);

    private static readonly QuestionDto Question = new(
        "01KBZ3Q8N9V2K3M4P5R6S7T8WA", "closure-for-loop", "csharp", "C#", "junior", "find-bug", "Title",
        "<p>Prompt</p>", "<p>Hint</p>", ["<span class=\"line\" data-line=\"1\">a</span>"], "<pre></pre>",
        [new OptionDto("a", "<p>A</p>", true, "<p>Why</p>"), new OptionDto("b", "<p>B</p>", false, null)],
        ["333"], [[1, 2], [4]], "<p>Answer</p>", "<p>Explanation</p>",
        [new LinkDto("https://learn.microsoft.com/dotnet", "Docs", "en")]);

    private static readonly TopicProgressDto TopicProgress = new("csharp", "C#", 12, 5, 3, 2);

    private static readonly MockSummaryDto Summary =
        new("dotnet", "middle", 2, true, At.AddMinutes(-30), At, [new MockTopicResultDto("csharp", 1, 1, 0, 0)]);

    private static readonly MockProgress Progress = new(
        MockProgress.CurrentVersion, "01KBZ3Q8N9V2K3M4P5R6S7T8WD", "123456789012", "dotnet", "en", "middle", 2, true,
        At.AddMinutes(-30), At.AddMinutes(60), "assessment", 1, 7,
        [new AttemptState("01KBZ3Q8N9V2K3M4P5R6S7T8WC", ["b", "a"], ["a"], [2, 3], "333", "notes", true, At.AddMinutes(-5), true, "partial", At.AddMinutes(-4), "knew", At.AddMinutes(-1), false)],
        new MockCompletionState(At, [new AnswerDto("01KBZ3Q8N9V2K3M4P5R6S7T8WC", "01KBZ3Q8N9V2K3M4P5R6S7T8WA", "partial", true, "mock", "01KBZ3Q8N9V2K3M4P5R6S7T8WD", At)], Summary, true, false));

    /// <summary>One sample per wire type; the coverage test fails when a type is added without one.</summary>
    private static readonly object[] Samples =
    [
        Question,
        Question.Options[0],
        Question.Links[0],
        new PracticeSessionRequest("en", "dotnet", ["junior"], ["csharp"], ["open", "find-bug"], "new", 20, "01KBZ3Q8N9V2K3M4P5R6S7T8WA", ["01KBZ3Q8N9V2K3M4P5R6S7T8WB"]),
        new MockSessionRequest("ru", "dotnet", "middle", [], 20),
        new ReviewSessionRequest("en", "dotnet", 20),
        new SessionDto([Question], 41),
        new MockSessionDto([Question], 14, true, 5),
        new AnswerDto("01KBZ3Q8N9V2K3M4P5R6S7T8WC", "01KBZ3Q8N9V2K3M4P5R6S7T8WA", "didnt-know", true, "mock", "01KBZ3Q8N9V2K3M4P5R6S7T8WD", At),
        new AnswerBatch(AnswerBatch.CurrentVersion, [new AnswerDto("01KBZ3Q8N9V2K3M4P5R6S7T8WC", "01KBZ3Q8N9V2K3M4P5R6S7T8WA", "knew", false, "practice", null, At)]),
        new AnswerBatchResponse(["01KBZ3Q8N9V2K3M4P5R6S7T8WC"], [new RejectedAnswerDto("01KBZ3Q8N9V2K3M4P5R6S7T8WE", "answered-too-old")]),
        new RejectedAnswerDto("01KBZ3Q8N9V2K3M4P5R6S7T8WE", "answered-in-future"),
        new MockSummaryDto("dotnet", "middle", 20, true, At.AddMinutes(-30), At, [new MockTopicResultDto("csharp", 5, 2, 1, 0)]),
        new MockTopicResultDto("csharp", 5, 2, 1, 0),
        new ProfileDto(4, 120, 7, At.AddDays(1), [TopicProgress], [new LevelProgressDto("junior", 30, 20, 15)], [new MockHistoryDto("01KBZ3Q8N9V2K3M4P5R6S7T8WD", "middle", 20, At, 10, 5, 3, 2)]),
        TopicProgress,
        new LevelProgressDto("junior", 30, 20, 15),
        new MockHistoryDto("01KBZ3Q8N9V2K3M4P5R6S7T8WD", "middle", 20, At, 10, 5, 3, 2),
        new AntiforgeryTokenDto("token", "X-CSRF-TOKEN"),
        new ApiErrorDto(ApiErrorCodes.Antiforgery, null),
        new RatingRequest("like"),
        new RatingDto(null),
        new ReportRequest("en", "wrong-answer", "typo"),
        new ReportResponse(ReportResponse.Created),
        new TopicDto("csharp", "C#"),
        new MeDto(123456789012L, 4, 7, [TopicProgress]),
        new PracticeFiltersDto(["junior"], [], ["open"], "all", null),
        new IslandBootstrapDto("en", "dotnet", [new TopicDto("csharp", "C#")], ["junior", "middle"], ["open", "single"],
            new PracticeFiltersDto([], [], [], "all", null), new MeDto(123456789012L, 4, 7, [TopicProgress])),
        Progress,
        Progress.Attempts[0],
        Progress.Completion!,
        new MockSummaryList(MockSummaryList.CurrentVersion, [new StoredMockSummary("01KBZ3Q8N9V2K3M4P5R6S7T8WD", Summary, "validation")]),
        new StoredMockSummary("01KBZ3Q8N9V2K3M4P5R6S7T8WD", Summary, null),
        new StoredMockQuestions(StoredMockQuestions.CurrentVersion, "01KBZ3Q8N9V2K3M4P5R6S7T8WD", [Question]),
        new StoredPracticeFilters(StoredPracticeFilters.CurrentVersion, ["middle"], ["async"], ["single"], "new", 12),
    ];

    private static Type[] WireTypes() =>
        [.. typeof(TrainerJsonContext).Assembly.GetExportedTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is null)
            .Where(t => t.GetMethod("<Clone>$") is not null)];

    [Fact]
    public void ContractsJson_RoundTripsEveryDtoWithSourceGeneratedContext()
    {
        foreach (object sample in Samples)
        {
            JsonTypeInfo typeInfo = TrainerJsonContext.Default.GetTypeInfo(sample.GetType())
                ?? throw new InvalidOperationException($"{sample.GetType().Name} is not registered in TrainerJsonContext.");

            string json = JsonSerializer.Serialize(sample, typeInfo);
            object? copy = JsonSerializer.Deserialize(json, typeInfo);

            Assert.NotNull(copy);
            Assert.Equal(json, JsonSerializer.Serialize(copy, typeInfo));
            AssertCamelCase(json);
        }
    }

    [Fact]
    public void ContractsJson_SessionRatings_KeepQuestionIdsAsDictionaryKeys()
    {
        SessionDto session = new([Question], 41, new Dictionary<string, string> { ["01KBZ3Q8N9V2K3M4P5R6S7T8WA"] = "like" });

        string json = JsonSerializer.Serialize(session, TrainerJsonContext.Default.SessionDto);
        SessionDto? copy = JsonSerializer.Deserialize(json, TrainerJsonContext.Default.SessionDto);

        // The id is data, not a property name, so the camel-case policy must leave it alone.
        Assert.Contains("\"myRatings\":{\"01KBZ3Q8N9V2K3M4P5R6S7T8WA\":\"like\"}", json, StringComparison.Ordinal);
        Assert.Equal("like", copy!.MyRatings!["01KBZ3Q8N9V2K3M4P5R6S7T8WA"]);
    }

    [Fact]
    public void ContractsJson_HasASampleAndARegistrationForEveryRecord()
    {
        Type[] wireTypes = WireTypes();

        Assert.NotEmpty(wireTypes);
        Assert.Empty(wireTypes.Where(t => TrainerJsonContext.Default.GetTypeInfo(t) is null).Select(t => t.Name));
        Assert.Empty(wireTypes.Except(Samples.Select(s => s.GetType())).Select(t => t.Name));
    }

    [Fact]
    public void ContractsJson_AnswerBatchEqualsTheSpecOutboxShape()
    {
        AnswerBatch batch = new(
            AnswerBatch.CurrentVersion,
            [
                new AnswerDto("01KBZ3Q8N9V2K3M4P5R6S7T8WC", "01KBZ3Q8N9V2K3M4P5R6S7T8WA", "knew", false, "practice", null, At),
                new AnswerDto("01KBZ3Q8N9V2K3M4P5R6S7T8WD", "01KBZ3Q8N9V2K3M4P5R6S7T8WB", "partial", true, "mock", "01KBZ3Q8N9V2K3M4P5R6S7T8WF", At),
            ]);

        string json = JsonSerializer.Serialize(batch, TrainerJsonContext.Default.AnswerBatch);

        Assert.Equal(
            "{\"version\":1,\"answers\":["
            + "{\"clientAnswerId\":\"01KBZ3Q8N9V2K3M4P5R6S7T8WC\",\"questionId\":\"01KBZ3Q8N9V2K3M4P5R6S7T8WA\",\"grade\":\"knew\",\"usedHint\":false,\"mode\":\"practice\",\"mockSessionId\":null,\"answeredAt\":\"2026-11-12T10:30:00+00:00\"},"
            + "{\"clientAnswerId\":\"01KBZ3Q8N9V2K3M4P5R6S7T8WD\",\"questionId\":\"01KBZ3Q8N9V2K3M4P5R6S7T8WB\",\"grade\":\"partial\",\"usedHint\":true,\"mode\":\"mock\",\"mockSessionId\":\"01KBZ3Q8N9V2K3M4P5R6S7T8WF\",\"answeredAt\":\"2026-11-12T10:30:00+00:00\"}"
            + "]}",
            json);
    }

    [Fact]
    public void ContractsJson_MissingAnsweredAtDeserializesToNull()
    {
        const string json = "{\"clientAnswerId\":\"a\",\"questionId\":\"b\",\"grade\":\"knew\",\"usedHint\":false,\"mode\":\"practice\"}";

        AnswerDto? answer = JsonSerializer.Deserialize(json, TrainerJsonContext.Default.AnswerDto);

        Assert.NotNull(answer);
        Assert.Null(answer.AnsweredAt);
        Assert.Null(answer.MockSessionId);
    }

    private static void AssertCamelCase(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        Walk(document.RootElement);

        static void Walk(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        Assert.True(char.IsLower(property.Name[0]), $"{property.Name} is not camelCase");
                        Walk(property.Value);
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (JsonElement item in element.EnumerateArray())
                    {
                        Walk(item);
                    }

                    break;
            }
        }
    }
}
