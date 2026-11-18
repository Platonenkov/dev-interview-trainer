using Trainer.Contracts;
using Trainer.Web.Client.Api;
using Trainer.Web.Client.Backend;

namespace Trainer.Web.Client.Tests;

/// <summary>Builds <see cref="SessionPlan"/> instances for the session tests.</summary>
internal static class TestPlans
{
    /// <summary>A plan whose questions are all preloaded, as the server backend builds it.</summary>
    public static SessionPlan Preloaded(params QuestionDto[] questions) =>
        new([.. questions.Select(q => q.Id)], questions.Length, null, new PreloadedQuestionSource(questions));
}

/// <summary>
/// A source that records every call and lets a test decide when, and whether, each question arrives. A question without
/// a gate completes at once.
/// </summary>
internal sealed class RecordingQuestionSource(IReadOnlyList<QuestionDto> questions) : IQuestionSource
{
    private readonly Dictionary<string, TaskCompletionSource<ApiResult<QuestionDto>>> _gates = new(StringComparer.Ordinal);

    public List<string> Calls { get; } = [];

    /// <summary>The next lookup of <paramref name="questionId"/> waits until the returned source is completed.</summary>
    public TaskCompletionSource<ApiResult<QuestionDto>> Hold(string questionId)
    {
        TaskCompletionSource<ApiResult<QuestionDto>> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _gates[questionId] = gate;
        return gate;
    }

    public async ValueTask<ApiResult<QuestionDto>> GetAsync(string questionId, CancellationToken ct)
    {
        Calls.Add("get:" + questionId);
        if (_gates.Remove(questionId, out TaskCompletionSource<ApiResult<QuestionDto>>? gate))
        {
            return await gate.Task;
        }

        return new ApiResult<QuestionDto>(questions.First(q => q.Id == questionId), 200, null);
    }

    public void Prefetch(string questionId) => Calls.Add("prefetch:" + questionId);

    public SessionPlan Plan() => new([.. questions.Select(q => q.Id)], questions.Count, null, this);
}
