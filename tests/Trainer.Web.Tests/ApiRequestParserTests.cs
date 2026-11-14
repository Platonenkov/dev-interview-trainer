using Trainer.Application.Answers;
using Trainer.Application.Mock;
using Trainer.Application.Sessions;
using Trainer.Contracts;
using Trainer.Domain.Grading;
using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Web.Api;
using Trainer.Web.ContentState;

namespace Trainer.Web.Tests;

/// <summary>Structural validation of <c>/api</c> bodies before anything reaches a service.</summary>
public sealed class ApiRequestParserTests
{
    private const string QuestionA = "01KBZ3Q8N9V2K3M4P5R6S7T8WA";
    private const string MockId = "01KC0000000000000000000M0C";

    private static readonly DateTimeOffset At = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private static readonly HashSet<TopicKey> Topics = [new("async"), new("csharp")];

    private static readonly ContentSnapshot Content = new(1, new Dictionary<StackKey, IReadOnlyList<TopicKey>>
    {
        [new StackKey("dotnet")] = [new TopicKey("async"), new TopicKey("csharp")],
    });

    private static int _nextId;

    private static string NewId() => "01KC00000000000000000" + Interlocked.Increment(ref _nextId).ToString("D5", System.Globalization.CultureInfo.InvariantCulture);

    private static AnswerDto Answer(string? id = null) => new(id ?? NewId(), QuestionA, "knew", false, "practice", null, At);

    private static AnswerBatch Batch(params AnswerDto?[] answers) => new(AnswerBatch.CurrentVersion, answers!);

    [Fact]
    public void Batch_NullEnvelope_IsValidationError()
    {
        Assert.False(ApiRequestParser.TryParseBatch(null, out _, out _, out string? error));
        Assert.Equal(ApiErrorCodes.Validation, error);
    }

    [Fact]
    public void Batch_NullAnswers_IsValidationError()
    {
        Assert.False(ApiRequestParser.TryParseBatch(new AnswerBatch(1, null!), out _, out _, out string? error));
        Assert.Equal(ApiErrorCodes.Validation, error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Batch_WrongVersion_IsValidationError(int version)
    {
        Assert.False(ApiRequestParser.TryParseBatch(new AnswerBatch(version, [Answer()]), out _, out _, out string? error));
        Assert.Equal(ApiErrorCodes.Validation, error);
    }

    [Fact]
    public void Batch_Over500_IsBatchTooLarge()
    {
        AnswerDto[] answers = [.. Enumerable.Range(0, IAnswerRecorder.MaxBatchSize + 1).Select(_ => Answer())];

        Assert.False(ApiRequestParser.TryParseBatch(Batch(answers), out _, out _, out string? error));
        Assert.Equal(ApiErrorCodes.BatchTooLarge, error);
    }

    [Fact]
    public void Batch_Exactly500_Parses()
    {
        AnswerDto[] answers = [.. Enumerable.Range(0, IAnswerRecorder.MaxBatchSize).Select(_ => Answer())];

        Assert.True(ApiRequestParser.TryParseBatch(Batch(answers), out IReadOnlyList<ReportedAnswer>? parsed, out _, out _));
        Assert.Equal(IAnswerRecorder.MaxBatchSize, parsed.Count);
    }

    [Fact]
    public void Batch_NullElement_RejectedWithEmptyId()
    {
        AnswerDto valid = Answer();

        Assert.True(ApiRequestParser.TryParseBatch(Batch(null, valid), out IReadOnlyList<ReportedAnswer>? parsed, out IReadOnlyList<RejectedAnswerDto>? rejected, out _));

        Assert.Equal([valid.ClientAnswerId], parsed.Select(a => a.ClientAnswerId.Value));
        Assert.Equal([new RejectedAnswerDto("", AnswerRejections.Invalid)], rejected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-ulid")]
    [InlineData("01kc0000000000000000000001")]
    public void Batch_InvalidClientAnswerId_RejectedWithEmptyId(string? id)
    {
        Assert.True(ApiRequestParser.TryParseBatch(Batch(Answer() with { ClientAnswerId = id! }), out IReadOnlyList<ReportedAnswer>? parsed, out IReadOnlyList<RejectedAnswerDto>? rejected, out _));

        Assert.Empty(parsed);
        Assert.Equal([new RejectedAnswerDto("", AnswerRejections.Invalid)], rejected);
    }

    public static TheoryData<string> InvalidFields() =>
        ["question-id", "null-question-id", "grade", "null-grade", "mode", "null-mode", "answered-at", "mock-id"];

    [Theory]
    [MemberData(nameof(InvalidFields))]
    public void Batch_InvalidField_RejectedById(string field)
    {
        AnswerDto valid = Answer();
        AnswerDto answer = field switch
        {
            "question-id" => valid with { QuestionId = "x" },
            "null-question-id" => valid with { QuestionId = null! },
            "grade" => valid with { Grade = "Knew" },
            "null-grade" => valid with { Grade = null! },
            "mode" => valid with { Mode = "exam" },
            "null-mode" => valid with { Mode = null! },
            "answered-at" => valid with { AnsweredAt = null },
            _ => valid with { Mode = "mock", MockSessionId = "nope" },
        };

        Assert.True(ApiRequestParser.TryParseBatch(Batch(answer), out IReadOnlyList<ReportedAnswer>? parsed, out IReadOnlyList<RejectedAnswerDto>? rejected, out _));

        Assert.Empty(parsed);
        Assert.Equal([new RejectedAnswerDto(answer.ClientAnswerId, AnswerRejections.Invalid)], rejected);
    }

    [Theory]
    [InlineData("mock", MockId, true)]
    [InlineData("mock", null, false)]
    [InlineData("practice", MockId, false)]
    [InlineData("review", MockId, false)]
    [InlineData("review", null, true)]
    public void Batch_ModeAndMockSessionIdMustAgree(string mode, string? mockId, bool valid)
    {
        AnswerDto answer = Answer() with { Mode = mode, MockSessionId = mockId };

        Assert.True(ApiRequestParser.TryParseBatch(Batch(answer), out IReadOnlyList<ReportedAnswer>? parsed, out IReadOnlyList<RejectedAnswerDto>? rejected, out _));

        Assert.Equal(valid ? 1 : 0, parsed.Count);
        Assert.Equal(valid ? 0 : 1, rejected.Count);
        if (valid)
        {
            Assert.Equal(mockId, parsed[0].MockSessionId?.Value);
        }
    }

    [Fact]
    public void Batch_ValidAnswer_IsFullyParsed()
    {
        AnswerDto answer = new(NewId(), QuestionA, "didnt-know", true, "mock", MockId, At);

        Assert.True(ApiRequestParser.TryParseBatch(Batch(answer), out IReadOnlyList<ReportedAnswer>? parsed, out _, out _));

        ReportedAnswer single = Assert.Single(parsed);
        Assert.Equal(answer.ClientAnswerId, single.ClientAnswerId.Value);
        Assert.Equal(QuestionA, single.QuestionId.Value);
        Assert.Equal(Grade.DidntKnow, single.Grade);
        Assert.True(single.UsedHint);
        Assert.Equal(AnswerMode.Mock, single.Mode);
        Assert.Equal(At, single.AnsweredAt);
    }

    [Fact]
    public void Batch_DuplicateIds_FirstOccurrenceWins()
    {
        string id = NewId();
        AnswerDto invalidFirst = Answer(id) with { Grade = "bad" };
        AnswerDto validSecond = Answer(id);
        string other = NewId();

        Assert.True(ApiRequestParser.TryParseBatch(
            Batch(invalidFirst, validSecond, Answer(other), Answer(other) with { Grade = "bad" }),
            out IReadOnlyList<ReportedAnswer>? parsed,
            out IReadOnlyList<RejectedAnswerDto>? rejected,
            out _));

        Assert.Equal([other], parsed.Select(a => a.ClientAnswerId.Value));
        Assert.Equal([new RejectedAnswerDto(id, AnswerRejections.Invalid)], rejected);
    }

    private static PracticeSessionRequest Practice() => new("en", "dotnet", ["middle"], ["async"], ["open"], "all", 10, null, []);

    public static TheoryData<string> InvalidPracticeRequests() =>
    [
        "null-levels", "null-topics", "null-types", "null-exclude", "null-level-element", "null-topic-element", "null-type-element",
        "null-exclude-element", "culture", "null-culture", "stack", "level", "type", "status", "null-status", "first-question", "excluded-id",
        "too-many-excluded", "only-unknown-topics",
    ];

    [Theory]
    [MemberData(nameof(InvalidPracticeRequests))]
    public void Practice_StructuralDefect_IsValidationError(string defect)
    {
        PracticeSessionRequest valid = Practice();
        PracticeSessionRequest request = defect switch
        {
            "null-levels" => valid with { Levels = null! },
            "null-topics" => valid with { Topics = null! },
            "null-types" => valid with { Types = null! },
            "null-exclude" => valid with { ExcludeQuestionIds = null! },
            "null-level-element" => valid with { Levels = [null!] },
            "null-topic-element" => valid with { Topics = ["async", null!] },
            "null-type-element" => valid with { Types = [null!] },
            "null-exclude-element" => valid with { ExcludeQuestionIds = [null!] },
            "culture" => valid with { Culture = "de" },
            "null-culture" => valid with { Culture = null! },
            "stack" => valid with { Stack = "Not A Key" },
            "level" => valid with { Levels = ["guru"] },
            "type" => valid with { Types = ["essay"] },
            "status" => valid with { Status = "later" },
            "null-status" => valid with { Status = null! },
            "first-question" => valid with { FirstQuestionId = "x" },
            "excluded-id" => valid with { ExcludeQuestionIds = ["x"] },
            "too-many-excluded" => valid with { Status = "new", ExcludeQuestionIds = [.. Enumerable.Range(0, SessionLimits.MaxExcludedQuestions + 1).Select(_ => NewId())] },
            _ => valid with { Topics = ["retired-topic"] },
        };

        Assert.False(ApiRequestParser.TryParsePractice(request, Topics, out _, out string? error));
        Assert.Equal(ApiErrorCodes.Validation, error);
    }

    [Fact]
    public void Practice_NullRequest_IsValidationError()
    {
        Assert.False(ApiRequestParser.TryParsePractice(null, Topics, out _, out string? error));
        Assert.Equal(ApiErrorCodes.Validation, error);
    }

    [Fact]
    public void Practice_UnknownTopicsDropped_KnownKept()
    {
        PracticeSessionRequest request = Practice() with { Topics = ["async", "retired-topic", "async"] };

        Assert.True(ApiRequestParser.TryParsePractice(request, Topics, out PracticeCriteria? criteria, out _));

        Assert.Equal([new TopicKey("async")], criteria.Topics);
    }

    [Theory]
    [InlineData(-5, SessionLimits.MinPracticeCount)]
    [InlineData(0, SessionLimits.MinPracticeCount)]
    [InlineData(7, 7)]
    [InlineData(1000, SessionLimits.MaxCount)]
    public void Practice_CountClamped(int requested, int expected)
    {
        Assert.True(ApiRequestParser.TryParsePractice(Practice() with { Count = requested }, Topics, out PracticeCriteria? criteria, out _));
        Assert.Equal(expected, criteria.Count);
    }

    [Fact]
    public void Practice_ValidRequest_IsFullyParsed()
    {
        PracticeSessionRequest request = new("ru", "dotnet", ["senior"], [], ["find-bug"], "new", 5, QuestionA, [QuestionA]);

        Assert.True(ApiRequestParser.TryParsePractice(request, Topics, out PracticeCriteria? criteria, out _));

        Assert.Equal("ru", criteria.Culture.Value);
        Assert.Equal("dotnet", criteria.Stack.Value);
        Assert.Equal([Level.Senior], criteria.Levels);
        Assert.Empty(criteria.Topics);
        Assert.Equal(PracticeStatus.New, criteria.Status);
        Assert.Equal(QuestionA, criteria.First?.Value);
        Assert.Equal([QuestionA], criteria.Exclude.Select(q => q.Value));
        Assert.Single(criteria.Types);
    }

    [Theory]
    [InlineData(0, SessionLimits.MinMockCount)]
    [InlineData(20, 20)]
    [InlineData(99, SessionLimits.MaxCount)]
    public void Mock_CountClampedToMockRange(int requested, int expected)
    {
        Assert.True(ApiRequestParser.TryParseMock(new MockSessionRequest("en", "dotnet", "middle", ["csharp", "unknown"], requested), Topics, out MockCriteria? criteria, out _));

        Assert.Equal(expected, criteria.Count);
        Assert.Equal(Level.Middle, criteria.Level);
        Assert.Equal([new TopicKey("csharp")], criteria.Topics);
    }

    public static TheoryData<string> InvalidMockRequests() => ["null", "null-topics", "null-topic-element", "level", "null-level", "culture", "only-unknown-topics"];

    [Theory]
    [MemberData(nameof(InvalidMockRequests))]
    public void Mock_StructuralDefect_IsValidationError(string defect)
    {
        MockSessionRequest valid = new("en", "dotnet", "middle", [], 20);
        MockSessionRequest? request = defect switch
        {
            "null" => null,
            "null-topics" => valid with { Topics = null! },
            "null-topic-element" => valid with { Topics = [null!] },
            "level" => valid with { Level = "guru" },
            "null-level" => valid with { Level = null! },
            "culture" => valid with { Culture = "xx" },
            _ => valid with { Topics = ["retired-topic"] },
        };

        Assert.False(ApiRequestParser.TryParseMock(request, Topics, out _, out string? error));
        Assert.Equal(ApiErrorCodes.Validation, error);
    }

    [Theory]
    [InlineData(0, SessionLimits.MinReviewCount)]
    [InlineData(12, 12)]
    [InlineData(51, SessionLimits.MaxCount)]
    public void Review_CountClamped(int requested, int expected)
    {
        Assert.True(ApiRequestParser.TryParseReview(new ReviewSessionRequest("en", "dotnet", requested), out ReviewCriteria? criteria, out _));
        Assert.Equal(expected, criteria.Count);
    }

    [Theory]
    [InlineData(null, "dotnet")]
    [InlineData("en", null)]
    [InlineData("xx", "dotnet")]
    public void Review_InvalidKeys_IsValidationError(string? culture, string? stack)
    {
        Assert.False(ApiRequestParser.TryParseReview(new ReviewSessionRequest(culture!, stack!, 5), out _, out string? error));
        Assert.Equal(ApiErrorCodes.Validation, error);
        Assert.False(ApiRequestParser.TryParseReview(null, out _, out _));
    }

    private static MockSummaryDto Summary() => new(
        "dotnet",
        "middle",
        5,
        Timed: false,
        At,
        At.AddMinutes(15),
        [new MockTopicResultDto("async", 2, 1, 1, 0), new MockTopicResultDto("csharp", 0, 0, 0, 1)]);

    [Fact]
    public void Summary_Valid_IsFullyParsed()
    {
        Assert.True(ApiRequestParser.TryParseSummary(Summary(), Content, out MockSummary? summary, out _));

        Assert.Equal("dotnet", summary.Stack.Value);
        Assert.Equal(Level.Middle, summary.Level);
        Assert.Equal(5, summary.QuestionCount);
        Assert.Equal(At.AddMinutes(15), summary.FinishedAt);
        Assert.Equal([new MockTopicResult(new TopicKey("async"), 2, 1, 1, 0), new MockTopicResult(new TopicKey("csharp"), 0, 0, 0, 1)], summary.Topics);
    }

    public static TheoryData<string> InvalidSummaries() =>
    [
        "null", "finished-before-started", "negative-knew", "negative-unanswered", "sum-above-count", "sum-below-count",
        "zero-question-count", "question-count-above-max", "unknown-topic", "duplicate-topic", "null-topics", "null-topic-element",
        "null-topic-key", "unknown-stack", "null-stack", "unknown-level", "null-level", "overflowing-counts",
    ];

    [Theory]
    [MemberData(nameof(InvalidSummaries))]
    public void Summary_StructuralDefect_IsValidationError(string defect)
    {
        MockSummaryDto valid = Summary();
        MockSummaryDto? summary = defect switch
        {
            "null" => null,
            "finished-before-started" => valid with { FinishedAt = valid.StartedAt.AddTicks(-1) },
            "negative-knew" => valid with { Topics = [new MockTopicResultDto("async", -1, 3, 3, 0)] },
            "negative-unanswered" => valid with { Topics = [new MockTopicResultDto("async", 3, 1, 2, -1)] },
            "sum-above-count" => valid with { QuestionCount = 4 },
            "sum-below-count" => valid with { QuestionCount = 6 },
            "zero-question-count" => valid with { QuestionCount = 0, Topics = [] },
            "question-count-above-max" => valid with { QuestionCount = 51, Topics = [new MockTopicResultDto("async", 51, 0, 0, 0)] },
            "unknown-topic" => valid with { Topics = [new MockTopicResultDto("retired-topic", 5, 0, 0, 0)] },
            "duplicate-topic" => valid with { Topics = [new MockTopicResultDto("async", 2, 0, 0, 0), new MockTopicResultDto("async", 3, 0, 0, 0)] },
            "null-topics" => valid with { Topics = null! },
            "null-topic-element" => valid with { Topics = [null!] },
            "null-topic-key" => valid with { Topics = [new MockTopicResultDto(null!, 5, 0, 0, 0)] },
            "unknown-stack" => valid with { Stack = "java" },
            "null-stack" => valid with { Stack = null! },
            "unknown-level" => valid with { Level = "guru" },
            "null-level" => valid with { Level = null! },
            _ => valid with { Topics = [new MockTopicResultDto("async", int.MaxValue, int.MaxValue, 2, 5)] },
        };

        Assert.False(ApiRequestParser.TryParseSummary(summary, Content, out _, out string? error));
        Assert.Equal(ApiErrorCodes.Validation, error);
    }
}
