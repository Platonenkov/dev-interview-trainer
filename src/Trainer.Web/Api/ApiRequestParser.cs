using System.Diagnostics.CodeAnalysis;
using Trainer.Application.Answers;
using Trainer.Application.Mock;
using Trainer.Application.Sessions;
using Trainer.Contracts;
using Trainer.Domain.Grading;
using Trainer.Domain.Ids;
using Trainer.Domain.Keys;
using Trainer.Domain.Levels;
using Trainer.Domain.Questions;
using Trainer.Domain.Sessions;
using Trainer.Web.ContentState;

namespace Trainer.Web.Api;

/// <summary>
/// Turns <c>/api</c> bodies into Application inputs. JSON binding does not enforce nullability or required members, so a
/// body can carry nulls anywhere; everything is checked here before any service sees it. A failed parse reports one of
/// <see cref="ApiErrorCodes"/>.
/// </summary>
public static class ApiRequestParser
{
    /// <summary>
    /// A null envelope or answer list, or a version other than <see cref="AnswerBatch.CurrentVersion"/>, is
    /// <c>validation</c>; more than <see cref="IAnswerRecorder.MaxBatchSize"/> answers is <c>batch-too-large</c>. Inside a
    /// valid envelope a bad entry never fails the batch: a null entry or one without a valid ULID client id is rejected as
    /// <c>("", "invalid")</c> (there is no id to acknowledge), an entry with any other invalid field as
    /// <c>(id, "invalid")</c>. The first entry with a given client id decides; later ones are dropped, as the recorder would.
    /// </summary>
    public static bool TryParseBatch(
        AnswerBatch? batch,
        [NotNullWhen(true)] out IReadOnlyList<ReportedAnswer>? answers,
        [NotNullWhen(true)] out IReadOnlyList<RejectedAnswerDto>? rejected,
        [NotNullWhen(false)] out string? error)
    {
        answers = null;
        rejected = null;
        if (batch?.Answers is null || batch.Version != AnswerBatch.CurrentVersion)
        {
            error = ApiErrorCodes.Validation;
            return false;
        }

        if (batch.Answers.Count > IAnswerRecorder.MaxBatchSize)
        {
            error = ApiErrorCodes.BatchTooLarge;
            return false;
        }

        List<ReportedAnswer> parsed = new(batch.Answers.Count);
        List<RejectedAnswerDto> invalid = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (AnswerDto? entry in batch.Answers)
        {
            if (entry is null || !ClientAnswerId.TryCreate(entry.ClientAnswerId, out ClientAnswerId id))
            {
                invalid.Add(new RejectedAnswerDto(string.Empty, AnswerRejections.Invalid));
                continue;
            }

            if (!seen.Add(id.Value))
            {
                continue;
            }

            if (TryParseAnswer(entry, id) is { } answer)
            {
                parsed.Add(answer);
            }
            else
            {
                invalid.Add(new RejectedAnswerDto(id.Value, AnswerRejections.Invalid));
            }
        }

        answers = parsed;
        rejected = invalid;
        error = null;
        return true;
    }

    /// <summary>
    /// Unknown topics are dropped (content may have changed since the page loaded), but a filter whose topics are all
    /// unknown is <c>validation</c>, because an empty list would mean "every topic". The count is clamped to
    /// <see cref="SessionLimits"/>; more than <see cref="SessionLimits.MaxExcludedQuestions"/> excluded ids is <c>validation</c>.
    /// </summary>
    public static bool TryParsePractice(
        PracticeSessionRequest? request,
        IReadOnlySet<TopicKey> knownTopics,
        [NotNullWhen(true)] out PracticeCriteria? criteria,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(knownTopics);
        criteria = null;
        error = ApiErrorCodes.Validation;
        if (request is null
            || !CultureKey.TryCreate(request.Culture, out CultureKey culture)
            || !StackKey.TryCreate(request.Stack, out StackKey stack)
            || !TryParseKeys<Level>(request.Levels, LevelKeys.TryParse, out IReadOnlyList<Level>? levels)
            || !TryParseKeys<QuestionType>(request.Types, QuestionTypeKeys.TryParse, out IReadOnlyList<QuestionType>? types)
            || !TryParseTopics(request.Topics, knownTopics, out IReadOnlyList<TopicKey>? topics)
            || !TryParseStatus(request.Status, out PracticeStatus status)
            || !TryParseOptionalQuestion(request.FirstQuestionId, out QuestionId? first)
            || request.ExcludeQuestionIds is null
            || request.ExcludeQuestionIds.Count > SessionLimits.MaxExcludedQuestions
            || !TryParseKeys<QuestionId>(request.ExcludeQuestionIds, QuestionId.TryCreate, out IReadOnlyList<QuestionId>? exclude))
        {
            return false;
        }

        int count = Math.Clamp(request.Count, SessionLimits.MinPracticeCount, SessionLimits.MaxCount);
        criteria = new PracticeCriteria(culture, stack, levels, topics, types, status, count, first, exclude);
        error = null;
        return true;
    }

    /// <summary>Like <see cref="TryParsePractice"/>; the level is required and the count is clamped to the mock range.</summary>
    public static bool TryParseMock(
        MockSessionRequest? request,
        IReadOnlySet<TopicKey> knownTopics,
        [NotNullWhen(true)] out MockCriteria? criteria,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(knownTopics);
        criteria = null;
        error = ApiErrorCodes.Validation;
        if (request is null
            || !CultureKey.TryCreate(request.Culture, out CultureKey culture)
            || !StackKey.TryCreate(request.Stack, out StackKey stack)
            || !LevelKeys.TryParse(request.Level, out Level level)
            || !TryParseTopics(request.Topics, knownTopics, out IReadOnlyList<TopicKey>? topics))
        {
            return false;
        }

        int count = Math.Clamp(request.Count, SessionLimits.MinMockCount, SessionLimits.MaxCount);
        criteria = new MockCriteria(culture, stack, level, topics, count);
        error = null;
        return true;
    }

    /// <summary>The count is clamped to <see cref="SessionLimits"/>.</summary>
    public static bool TryParseReview(
        ReviewSessionRequest? request,
        [NotNullWhen(true)] out ReviewCriteria? criteria,
        [NotNullWhen(false)] out string? error)
    {
        criteria = null;
        error = ApiErrorCodes.Validation;
        if (request is null
            || !CultureKey.TryCreate(request.Culture, out CultureKey culture)
            || !StackKey.TryCreate(request.Stack, out StackKey stack))
        {
            return false;
        }

        criteria = new ReviewCriteria(culture, stack, Math.Clamp(request.Count, SessionLimits.MinReviewCount, SessionLimits.MaxCount));
        error = null;
        return true;
    }

    /// <summary>
    /// A summary is stored verbatim, so it must be consistent: a stack and topics that exist in <paramref name="content"/>,
    /// a known level, <c>FinishedAt</c> not before <c>StartedAt</c>, a question count of 1..<see cref="SessionLimits.MaxCount"/>,
    /// each topic once with non-negative counts, and <c>Knew + Partial + DidntKnow + Unanswered</c> summed over the topics
    /// equal to the question count. Whether the times are plausible is judged later by the mock session service.
    /// </summary>
    public static bool TryParseSummary(
        MockSummaryDto? summary,
        ContentSnapshot content,
        [NotNullWhen(true)] out MockSummary? parsed,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(content);
        parsed = null;
        error = ApiErrorCodes.Validation;
        if (summary?.Topics is null
            || !content.HasStack(summary.Stack)
            || !StackKey.TryCreate(summary.Stack, out StackKey stack)
            || !LevelKeys.TryParse(summary.Level, out Level level)
            || summary.QuestionCount is < 1 or > SessionLimits.MaxCount
            || summary.FinishedAt < summary.StartedAt
            || !TryParseTopicResults(summary.Topics, summary.Stack, content, out List<MockTopicResult>? topics, out long total)
            || total != summary.QuestionCount)
        {
            return false;
        }

        parsed = new MockSummary(stack, level, summary.QuestionCount, summary.Timed, summary.StartedAt, summary.FinishedAt, topics);
        error = null;
        return true;
    }

    private static ReportedAnswer? TryParseAnswer(AnswerDto entry, ClientAnswerId id)
    {
        if (!QuestionId.TryCreate(entry.QuestionId, out QuestionId question)
            || !GradeKeys.TryParse(entry.Grade, out Grade grade)
            || !AnswerModeKeys.TryParse(entry.Mode, out AnswerMode mode)
            || entry.AnsweredAt is not { } answeredAt)
        {
            return null;
        }

        // Mock answers belong to a mock session and nothing else does.
        MockSessionId? mockSession = null;
        if (mode == AnswerMode.Mock)
        {
            if (!MockSessionId.TryCreate(entry.MockSessionId, out MockSessionId parsed))
            {
                return null;
            }

            mockSession = parsed;
        }
        else if (entry.MockSessionId is not null)
        {
            return null;
        }

        return new ReportedAnswer(id, question, grade, entry.UsedHint, mode, mockSession, answeredAt);
    }

    private delegate bool KeyParser<T>(string? value, out T result);

    /// <summary>Every element must parse; duplicates are removed, first occurrence first.</summary>
    private static bool TryParseKeys<T>(IReadOnlyList<string>? values, KeyParser<T> parse, [NotNullWhen(true)] out IReadOnlyList<T>? keys)
    {
        keys = null;
        if (values is null)
        {
            return false;
        }

        List<T> parsed = new(values.Count);
        HashSet<T> seen = [];
        foreach (string? value in values)
        {
            if (!parse(value, out T key))
            {
                return false;
            }

            if (seen.Add(key))
            {
                parsed.Add(key);
            }
        }

        keys = parsed;
        return true;
    }

    private static bool TryParseTopics(IReadOnlyList<string>? values, IReadOnlySet<TopicKey> knownTopics, [NotNullWhen(true)] out IReadOnlyList<TopicKey>? topics)
    {
        topics = null;
        if (!TryParseKeys<TopicKey>(values, TopicKey.TryCreate, out IReadOnlyList<TopicKey>? requested))
        {
            return false;
        }

        List<TopicKey> known = [.. requested.Where(knownTopics.Contains)];
        if (requested.Count > 0 && known.Count == 0)
        {
            return false;
        }

        topics = known;
        return true;
    }

    private static bool TryParseStatus(string? value, out PracticeStatus status)
    {
        switch (value)
        {
            case PracticeStatusKeys.All:
                status = PracticeStatus.All;
                return true;
            case PracticeStatusKeys.New:
                status = PracticeStatus.New;
                return true;
            case PracticeStatusKeys.Due:
                status = PracticeStatus.Due;
                return true;
            default:
                status = default;
                return false;
        }
    }

    private static bool TryParseOptionalQuestion(string? value, out QuestionId? question)
    {
        question = null;
        if (value is null)
        {
            return true;
        }

        if (!QuestionId.TryCreate(value, out QuestionId parsed))
        {
            return false;
        }

        question = parsed;
        return true;
    }

    private static bool TryParseTopicResults(
        IReadOnlyList<MockTopicResultDto> values,
        string stack,
        ContentSnapshot content,
        [NotNullWhen(true)] out List<MockTopicResult>? topics,
        out long total)
    {
        topics = null;
        total = 0;
        List<MockTopicResult> parsed = new(values.Count);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (MockTopicResultDto? value in values)
        {
            if (value is null
                || !content.HasTopic(stack, value.Topic)
                || !TopicKey.TryCreate(value.Topic, out TopicKey topic)
                || !seen.Add(topic.Value)
                || value.Knew < 0 || value.Partial < 0 || value.DidntKnow < 0 || value.Unanswered < 0)
            {
                return false;
            }

            // Long arithmetic: four int counts cannot overflow it, so huge counts are caught by the sum check.
            total += (long)value.Knew + value.Partial + value.DidntKnow + value.Unanswered;
            parsed.Add(new MockTopicResult(topic, value.Knew, value.Partial, value.DidntKnow, value.Unanswered));
        }

        topics = parsed;
        return true;
    }
}
