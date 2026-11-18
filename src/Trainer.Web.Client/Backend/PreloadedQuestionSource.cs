using Trainer.Contracts;
using Trainer.Web.Client.Api;

namespace Trainer.Web.Client.Backend;

/// <summary>A source over questions that arrived together with the session; every lookup completes synchronously.</summary>
public sealed class PreloadedQuestionSource : IQuestionSource
{
    private readonly Dictionary<string, QuestionDto> _byId = new(StringComparer.Ordinal);

    public PreloadedQuestionSource(IReadOnlyList<QuestionDto> questions)
    {
        ArgumentNullException.ThrowIfNull(questions);
        foreach (QuestionDto question in questions)
        {
            // A repeated id keeps its first question, as the session shows the same one for both positions.
            _byId.TryAdd(question.Id, question);
        }
    }

    public ValueTask<ApiResult<QuestionDto>> GetAsync(string questionId, CancellationToken ct) =>
        ValueTask.FromResult(_byId.TryGetValue(questionId, out QuestionDto? question)
            ? new ApiResult<QuestionDto>(question, 200, null)
            : new ApiResult<QuestionDto>(null, 404, ApiErrorCodes.QuestionNotFound));

    public void Prefetch(string questionId)
    {
    }
}
